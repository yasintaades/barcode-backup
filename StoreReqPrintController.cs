using Microsoft.AspNetCore.Mvc;
using Npgsql;
using Dapper;
using System.Text.Json.Serialization;

namespace Barcode.Api.Controllers
{
    [ApiController]
    [Route("api/barcode/store")]
    public class StoreRequestPrintController : ControllerBase
    {
        private readonly IConfiguration _config;
        public StoreRequestPrintController(IConfiguration config) => _config = config;

        public class StoreCreateDto
        {
            [JsonPropertyName("requested_by")] public string RequestedBy { get; set; }
            [JsonPropertyName("items")] public List<StoreItemDto> Items { get; set; }
        }

        public class StoreItemDto
        {
            [JsonPropertyName("barcode")] public string Barcode { get; set; }
            [JsonPropertyName("qty")] public int Qty { get; set; }
            
            // 💡 1. TAMBAHKAN INI: Agar objek C# dapat menampung notes per item dari DB
            [JsonPropertyName("notes_am")] public string NotesAm { get; set; }
        }

        // ==========================================
        // CREATE: Toko membuat pengajuan baru
        // ==========================================
        [HttpPost("request-print/create")]
        public async Task<IActionResult> CreateRequest([FromBody] StoreCreateDto dto)
        {
            if (dto == null || dto.Items == null || !dto.Items.Any())
                return BadRequest(new { success = false, message = "Data item kosong." });

            using var conn = new NpgsqlConnection(_config.GetConnectionString("DefaultConnection"));
            await conn.OpenAsync();

            // 1. Validasi: Ambil daftar barcode dari input user
            var inputBarcodes = dto.Items.Select(x => x.Barcode).Distinct().ToList();

            // 2. Query ke database untuk mencari barcode yang VALID
            // Ganti 'po_details' dengan nama tabel master barang Anda yang benar
            var sqlCheck = "SELECT barcode FROM po_details WHERE barcode = ANY(@Barcodes)";
            var existingBarcodes = (await conn.QueryAsync<string>(sqlCheck, new { Barcodes = inputBarcodes })).ToList();

            // 3. Bandingkan: Jika ada barcode di input tapi tidak ada di DB, maka gagal
            var missingBarcodes = inputBarcodes.Except(existingBarcodes).ToList();
            if (missingBarcodes.Any())
            {
                return BadRequest(new { 
                    success = false, 
                    message = $"Gagal diajukan. Barcode berikut tidak terdaftar: {string.Join(", ", missingBarcodes)}" 
                });
            }

            // 4. Jika lolos validasi, baru jalankan transaksi simpan data
            using var tx = await conn.BeginTransactionAsync();
            try
            {
                var dateStr = DateTime.Now.ToString("yyyyMMdd");
                var generatedRequestNo = $"REQ-{dateStr}-{new Random().Next(1000, 9999)}";

                await conn.ExecuteAsync(@"
                    INSERT INTO request_print_headers (request_no, po_status, requested_by, created_at)
                    VALUES (@RequestNo, 'PENDING_AM', @RequestedBy, NOW())",
                    new { RequestNo = generatedRequestNo, RequestedBy = dto.RequestedBy }, tx);

                // Menggunakan bulk insert agar lebih rapi (opsional)
                var insertDetailSql = @"
                    INSERT INTO request_print_details (request_no, barcode, qty, printed_qty)
                    VALUES (@RequestNo, @Barcode, @Qty, 0)";
                
                await conn.ExecuteAsync(insertDetailSql, dto.Items.Select(item => new {
                    RequestNo = generatedRequestNo,
                    item.Barcode,
                    item.Qty
                }), tx);

                await tx.CommitAsync();
                return Ok(new { success = true, request_no = generatedRequestNo, message = "Pengajuan berhasil dibuat!" });
            }
            catch (Exception ex)
            {
                await tx.RollbackAsync();
                return StatusCode(500, new { success = false, message = ex.Message });
            }
        }
       // ==========================================
        // UPDATE: Toko mengedit pengajuan (Diizinkan untuk PENDING_AM, REVISION, REVISED)
        // ==========================================
        [HttpPut("request-print/{requestNo}")]
        public async Task<IActionResult> EditRequest(string requestNo, [FromBody] StoreCreateDto dto)
        {
            if (dto == null || dto.Items == null || !dto.Items.Any())
                return BadRequest(new { success = false, message = "Data item revisi tidak boleh kosong." });

            using var conn = new NpgsqlConnection(_config.GetConnectionString("DefaultConnection"));
            await conn.OpenAsync();

            var status = await conn.QueryFirstOrDefaultAsync<string>(
                "SELECT po_status FROM request_print_headers WHERE request_no = @requestNo", new { requestNo });

            if (status == null) return NotFound(new { success = false, message = "Data tidak ditemukan." });
            
            var allowedStatuses = new[] { "PENDING_AM", "REVISION", "REVISED" };
            if (!allowedStatuses.Contains(status)) 
                return BadRequest(new { success = false, message = $"Data terkunci! Status sudah {status}." });

            using var tx = await conn.BeginTransactionAsync();
            try
            {
                // 💡 PERBAIKAN DI SINI: Status diubah menjadi 'PENDING_AM' agar otomatis kembali ke antrean Area Manager
                await conn.ExecuteAsync(@"
                    UPDATE request_print_headers 
                    SET po_status = 'PENDING_AM', updated_at = NOW() 
                    WHERE request_no = @requestNo", new { requestNo }, tx);

                // Hapus detail lama
                await conn.ExecuteAsync("DELETE FROM request_print_details WHERE request_no = @requestNo", new { requestNo }, tx);
                
                // RE-INSERT DETAIL: Masukkan kembali item baru beserta notes_am bawaannya agar catatan lama tidak hilang/menjadi NULL
                foreach (var item in dto.Items)
                {
                    await conn.ExecuteAsync(@"
                        INSERT INTO request_print_details (request_no, barcode, qty, printed_qty, notes_am)
                        VALUES (@requestNo, @Barcode, @Qty, 0, @NotesAm)",
                        new { requestNo, item.Barcode, item.Qty, NotesAm = item.NotesAm }, tx);
                }

                await tx.CommitAsync();
                return Ok(new { success = true, message = "Perbaikan revisi dokumen berhasil dikirim kembali!" });
            }
            catch (Exception ex)
            {
                await tx.RollbackAsync();
                return StatusCode(500, new { success = false, message = ex.Message });
            }
        }
        

        // ==========================================
        // DELETE: Toko membatalkan/menghapus pengajuan
        // ==========================================
        [HttpDelete("request-print/{requestNo}")]
        public async Task<IActionResult> DeleteRequest(string requestNo)
        {
            using var conn = new NpgsqlConnection(_config.GetConnectionString("DefaultConnection"));
            await conn.OpenAsync();

            var status = await conn.QueryFirstOrDefaultAsync<string>(
                "SELECT po_status FROM request_print_headers WHERE request_no = @requestNo", new { requestNo });

            if (status == null) return NotFound(new { success = false, message = "Data tidak ditemukan." });
            
            // 💡 Izinkan hapus untuk status draft pengajuan maupun dokumen reject/revisi
            var allowedDeleteStatuses = new[] { "PENDING_AM", "REVISION", "REVISED", "REJECTED" };
            if (!allowedDeleteStatuses.Contains(status)) 
                return BadRequest(new { success = false, message = "Gagal. Dokumen sedang diproses." });

            using var tx = await conn.BeginTransactionAsync();
            try
            {
                await conn.ExecuteAsync("DELETE FROM request_print_details WHERE request_no = @requestNo", new { requestNo }, tx);
                await conn.ExecuteAsync("DELETE FROM request_print_headers WHERE request_no = @requestNo", new { requestNo }, tx);
                await tx.CommitAsync();

                return Ok(new { success = true, message = "Pengajuan berhasil dihapus." });
            }
            catch (Exception ex)
            {
                await tx.RollbackAsync();
                return StatusCode(500, new { success = false, message = ex.Message });
            }
        }

        // ==========================================
        // GET LIST: Mengambil riwayat pengajuan per toko
        // ==========================================
        [HttpGet("request-print/list")]
        public async Task<IActionResult> GetStoreHistory([FromQuery] string store)
        {
            if (string.IsNullOrEmpty(store))
            {
                return BadRequest(new { success = false, message = "Parameter nama/ID toko (store) wajib diisi." });
            }

            using var conn = new NpgsqlConnection(_config.GetConnectionString("DefaultConnection"));
            
            // 💡 2. UPDATE QUERY: Tambahkan d.notes_am agar ditarik dari database PostgreSQL
            var sql = @"
                SELECT 
                    h.request_no AS RequestNo, 
                    h.po_status AS Status, 
                    h.requested_by AS RequestedBy, 
                    h.created_at AS CreatedAt,
                    d.barcode AS Barcode, 
                    d.qty AS Qty,
                    d.notes_am AS NotesAm
                FROM request_print_headers h
                INNER JOIN request_print_details d ON h.request_no = d.request_no
                WHERE h.requested_by = @Store
                ORDER BY h.created_at DESC";

            try
            {
                var rawResult = await conn.QueryAsync<dynamic>(sql, new { Store = store });

                // 💡 3. MAP DATA: Masukkan i.notesam ke dalam mapping list items JSON
                var groupedResult = rawResult.GroupBy(x => x.requestno).Select(g => new
                {
                    request_no = g.Key,
                    requested_by = g.First().requestedby,
                    status = g.First().status,
                    created_at = g.First().createdat,
                    items = g.Select(i => new
                    {
                        barcode = i.barcode,
                        qty = i.qty,
                        notes_am = i.notesam // Dapper dynamic merubah nama properti menjadi lowercase tanpa underscore
                    }).ToList()
                }).ToList();

                return Ok(new { success = true, data = groupedResult });
            }
            catch (Exception ex)
            {
                return StatusCode(500, new { success = false, message = ex.Message });
            }
        }
    }
}
