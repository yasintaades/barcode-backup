using Barcode.Api.Models;
using Barcode.Api.Services;
using Dapper;
using Microsoft.Extensions.Hosting;
using Npgsql;

namespace Barcode.Api.Background
{
    public class RequestPrintWorker : BackgroundService
    {
        private readonly IConfiguration _config;
        private readonly IServiceScopeFactory _scopeFactory;

        public RequestPrintWorker(
            IConfiguration config,
            IServiceScopeFactory scopeFactory)
        {
            _config = config;
            _scopeFactory = scopeFactory;
            
            Console.WriteLine("[DEBUG-REQUEST] MASUK KEDALAM CONSTRUCTOR REQUESTPRINTWORKER SUKSES!");
        }

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            Console.WriteLine("[DEBUG-REQUEST] METHOD EXECUTEASYNC DI REQUESTPRINTWORKER RESMI DIMULAI.");

            while (!stoppingToken.IsCancellationRequested)
            {
                try
                {
                    using var scope = _scopeFactory.CreateScope();
                    var printService = scope.ServiceProvider.GetRequiredService<IPrintService>();

                    using var conn = new NpgsqlConnection(_config.GetConnectionString("DefaultConnection"));
                    await conn.OpenAsync(stoppingToken);
                    
                    // Mengambil 1 item detail yang perlu dicetak dari dokumen yang statusnya WAITING
                    var queue = await conn.QueryFirstOrDefaultAsync<dynamic>(@"
                        SELECT 
                            d.id, 
                            d.request_no, 
                            d.barcode, 
                            d.qty,
                            d.printed_qty
                        FROM request_print_headers h
                        INNER JOIN request_print_details d ON h.request_no = d.request_no
                        WHERE h.po_status = 'WAITING'
                        AND d.qty > d.printed_qty
                        ORDER BY h.created_at ASC, d.id ASC
                        FOR UPDATE SKIP LOCKED
                        LIMIT 1
                    ");

                    if (queue == null)
                    {
                        await CekDanSelesaikanHeader(conn);

                        await Task.Delay(2000, stoppingToken);
                        continue;
                    }

                    Console.WriteLine($"[START-REQUEST] PROCESSING DETAIL ID: {queue.id} | DOC: {queue.request_no} | BARCODE: {queue.barcode}");

                    // ========================================================
                    // 2. AMBIL ATTRIBUTE BARCODE DARI PO_DETAILS
                    // ========================================================
                    var item = await conn.QueryFirstOrDefaultAsync<dynamic>(@"
                        SELECT
                            id,
                            barcode,
                            description,
                            price,
                            size,
                            color
                        FROM po_details
                        WHERE barcode = @barcode
                        AND status = 1
                        ORDER BY id DESC
                        LIMIT 1
                    ", new { barcode = queue.barcode });

                    if (item == null)
                    {
                        throw new Exception($"Barcode '{queue.barcode}' tidak aktif atau tidak ditemukan di po_details.");
                    }

                    // Logika pembulatan kelipatan 3 seperti sistem lama Anda
                    int qtyTarget = Convert.ToInt32(queue.qty);
                    int finalQty = (int)Math.Ceiling(qtyTarget / 3.0) * 3;

                    var labels = new List<PrintQueueItem>();
                    for (int i = 0; i < finalQty; i++)
                    {
                        labels.Add(new PrintQueueItem
                        {
                            Sku = item.barcode,
                            Desc = item.description,
                            Color = item.color,
                            Size = item.size,
                            Price = item.price,
                            DetailId = item.id
                        });
                    }

                    Console.WriteLine($"[DEBUG-REQUEST] TOTAL LABEL YANG SIAP DICETAK: {labels.Count} LEMBAR.");

                    var printerName = _config["PrinterSettings:DefaultPrinter"] ?? "ZDesigner ZT230-200dpi ZPL";
                    int currentPrinted = 0;

                    // ========================================================
                    // 3. PROSES CETAK PER 3 LABEL
                    // ========================================================
                    for (int i = 0; i < labels.Count; i += 3)
                    {
                        var group = labels.Skip(i).Take(3).ToList();
                        var zpl = printService.GenerateZplForThreeBarcodes(group);

                        Console.WriteLine($"[DEBUG-REQUEST] MENGIRIM KELOMPOK BARCODE KE WINDOWS SPOOLER ({printerName})...");
                        var ok = RawPrinterHelper.SendStringToPrinter(printerName, zpl);
                        Console.WriteLine($"[DEBUG-REQUEST] SPOOLER RESULT FOR DETAIL ID {queue.id}: {ok}");

                        if (!ok)
                        {
                            throw new Exception($"Windows Spooler menolak mencetak ke printer '{printerName}'.");
                        }

                        currentPrinted += group.Count;

                        await conn.ExecuteAsync(@"
                            UPDATE request_print_details
                            SET printed_qty = LEAST(qty, @printed)
                            WHERE id = @id
                        ", new { printed = currentPrinted, id = queue.id });

                        await Task.Delay(1000, stoppingToken);
                    }

                    Console.WriteLine($"[SUCCESS-REQUEST] DETAIL ID {queue.id} SELESAI DICETAK.");
                }
                catch (Exception ex)
                {
                    Console.WriteLine("[ERROR DI DALAM REQUESTPRINTWORKER EXECUTEASYNC]: " + ex.ToString());
                }

                await Task.Delay(2000, stoppingToken);
            }
        }

        private async Task CekDanSelesaikanHeader(NpgsqlConnection conn)
        {
            var finishedHeaders = await conn.QueryAsync<string>(@"
                SELECT h.request_no
                FROM request_print_headers h
                WHERE h.po_status = 'WAITING'
                AND NOT EXISTS (
                    SELECT 1 
                    FROM request_print_details d 
                    WHERE d.request_no = h.request_no 
                    AND d.qty > d.printed_qty
                )
            ");

            foreach (var reqNo in finishedHeaders)
            {
                await conn.ExecuteAsync(@"
                    UPDATE request_print_headers
                    SET po_status = 'DONE'
                    WHERE request_no = @requestNo
                ", new { requestNo = reqNo });

                Console.WriteLine($"[STATUS-UPDATE] Dokumen {reqNo} semua item selesai dicetak. Status berubah menjadi DONE.");
            }
        }
    }
}
