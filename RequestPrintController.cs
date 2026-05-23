using Microsoft.AspNetCore.Mvc;
using Npgsql;
using Dapper;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Configuration;

namespace Barcode.Api.Controllers
{
    [ApiController]
    [Route("api/barcode")]
    public class RequestPrintController : ControllerBase
    {
        private readonly IConfiguration _config;

        public RequestPrintController(IConfiguration config)
        {
            _config = config;
        }

        #region DTO & DATA STRUCTURES

        public class HistoryFilterDto
        {
            public string Search { get; set; }      
            public string Status { get; set; }      
            public DateTime? StartDate { get; set; } 
            public DateTime? EndDate { get; set; }   
            public int Page { get; set; } = 1;       // Tambahan untuk Pagination
            public int PageSize { get; set; } = 20;  // Tambahan untuk Pagination
        }

        public class CreateRequestDto
        {
            [JsonPropertyName("requested_by")]
            public string RequestedBy { get; set; }

            [JsonPropertyName("items")]
            public List<RequestItemDto> Items { get; set; }
        }

        public class RequestItemDto
        {
            [JsonPropertyName("barcode")]
            public string Barcode { get; set; }

            [JsonPropertyName("qty")]
            public int Qty { get; set; }
        }

        public class ApproveBulkPayload
        {
            [JsonPropertyName("request_no")]
            public string RequestNo { get; set; }
        }

        public class ReadyToPrintBulkPayload
        {
            [JsonPropertyName("request_no")]
            public string RequestNo { get; set; }
        }

        public class RawQueueItem
        {
            public string RequestNo { get; set; }
            public string Status { get; set; }
            public string RequestedBy { get; set; }
            public DateTime CreatedAt { get; set; }
            public DateTime? UpdatedAt { get; set; }
            
            // Detail Fields
            public int Id { get; set; }
            public string Barcode { get; set; }
            public int Qty { get; set; }
            public int PrintedQty { get; set; }
            public string NotesAm { get; set; }
            public string Description { get; set; }
            public decimal Price { get; set; }
            public string Color { get; set; }
            public string Size { get; set; }
            public string AsalPo { get; set; }
            public string NamaVendor { get; set; }
        }

        public class AmRejectDto
        {
            [JsonPropertyName("request_no")] public string RequestNo { get; set; }
        }

        public class RevisionPayload
        {
            [JsonPropertyName("request_no")]
            public string RequestNo { get; set; }

            [JsonPropertyName("items")]
            public List<RevisionItemDto> Items { get; set; }
        }

        public class RevisionItemDto
        {
            [JsonPropertyName("id")]
            public int Id { get; set; }

            [JsonPropertyName("notes_am")]
            public string NotesAm { get; set; }
        }

        #endregion

        // 1. ROLE TOKO: Membuat Pengajuan Cetak Barcode Baru
        [HttpPost("request-print/create")]
        public async Task<IActionResult> CreateRequest([FromBody] CreateRequestDto dto)
        {
            if (dto == null || dto.Items == null || !dto.Items.Any())
            {
                return BadRequest(new { success = false, message = "Data pengajuan kosong atau tidak valid." });
            }

            using var conn = new NpgsqlConnection(_config.GetConnectionString("DefaultConnection"));
            await conn.OpenAsync();
            using var transaction = await conn.BeginTransactionAsync();

            try
            {
                // Gunakan format timestamp presisi tinggi untuk mencegah duplikasi nomor request
                var dateStr = DateTime.Now.ToString("yyyyMMddHHmmssfff");
                var randomSuffix = new Random().Next(100, 999);
                var generatedRequestNo = $"REQ-{dateStr}-{randomSuffix}";

                var insertHeaderSql = @"
                    INSERT INTO request_print_headers (request_no, po_status, requested_by, created_at)
                    VALUES (@RequestNo, 'PENDING_AM', @RequestedBy, NOW())";

                await conn.ExecuteAsync(insertHeaderSql, new {
                    RequestNo = generatedRequestNo,
                    RequestedBy = dto.RequestedBy
                }, transaction);

                var insertDetailSql = @"
                    INSERT INTO request_print_details (request_no, barcode, qty, printed_qty)
                    VALUES (@RequestNo, @Barcode, @Qty, 0)";

                var detailParams = dto.Items.Select(item => new {
                    RequestNo = generatedRequestNo,
                    Barcode = item.Barcode,
                    Qty = item.Qty
                }).ToList();

                await conn.ExecuteAsync(insertDetailSql, detailParams, transaction);

                await transaction.CommitAsync();
                return Ok(new { success = true, message = "Semua barcode berhasil diajukan!", request_no = generatedRequestNo });
            }
            catch (Exception ex)
            {
                await transaction.RollbackAsync();
                return StatusCode(500, new { success = false, message = ex.Message });
            }
        }

        // 2. ROLE AREA MANAGER (AM): Melihat Antrean Butuh Approval
        [HttpGet("request-print/waiting-am")]
        public async Task<IActionResult> GetWaitingRequestsForAM()
        {
            using var conn = new NpgsqlConnection(_config.GetConnectionString("DefaultConnection"));
            await conn.OpenAsync();

            try
            {
                var sql = @"
                    SELECT 
                        h.request_no AS RequestNo,
                        h.po_status AS Status,
                        h.requested_by AS RequestedBy,
                        h.created_at AS CreatedAt,
                        d.id AS Id,
                        d.barcode AS Barcode,
                        d.qty AS Qty,
                        p.description AS Description,
                        p.price AS Price,
                        p.color AS Color,
                        p.size AS Size
                    FROM request_print_headers h
                    INNER JOIN request_print_details d ON h.request_no = d.request_no
                    LEFT JOIN (
                        SELECT DISTINCT ON (barcode) barcode, description, price, color, size 
                        FROM po_details 
                        WHERE status = 1
                        ORDER BY barcode, id DESC
                    ) p ON d.barcode = p.barcode
                    WHERE h.po_status = 'PENDING_AM'
                    ORDER BY h.created_at DESC";

                // Ubah dari dynamic ke strongly-typed RawQueueItem untuk menghindari isu case-sensitivity PostgreSQL
                var rawItems = await conn.QueryAsync<RawQueueItem>(sql);

                var groupedData = rawItems
                    .GroupBy(x => x.RequestNo)
                    .Select(g => {
                        var firstItem = g.First();
                        return new {
                            RequestNumber = g.Key,
                            RequestedBy = firstItem.RequestedBy,
                            CreatedAt = firstItem.CreatedAt,
                            TotalItems = g.Sum(x => x.Qty),
                            Items = g.Select(x => new {
                                Id = x.Id,
                                Barcode = x.Barcode,
                                Qty = x.Qty,
                                Status = x.Status,
                                Description = x.Description,
                                Price = x.Price,
                                Color = x.Color,
                                Size = x.Size
                            }).ToList()
                        };
                    }).ToList();

                return Ok(new { success = true, data = groupedData });
            }
            catch (Exception ex)
            {
                return StatusCode(500, new { success = false, message = ex.Message });
            }
        }

        // APPROVAL BY AM (Massal per Nomor Request)
        [HttpPost("request-print/approve-bulk")]
        public async Task<IActionResult> ApproveRequestBulk([FromBody] ApproveBulkPayload payload)
        {
            if (payload == null || string.IsNullOrEmpty(payload.RequestNo))
            {
                return BadRequest(new { success = false, message = "Nomor request tidak valid." });
            }

            using var conn = new NpgsqlConnection(_config.GetConnectionString("DefaultConnection"));
            await conn.OpenAsync();

            try
            {
                var sql = "UPDATE request_print_headers SET po_status = 'APPROVED', updated_at = NOW() WHERE request_no = @RequestNo AND po_status = 'PENDING_AM'";
                var affectedRows = await conn.ExecuteAsync(sql, new { RequestNo = payload.RequestNo });

                if (affectedRows == 0)
                    return BadRequest(new { success = false, message = "Data tidak ditemukan atau sudah diproses." });

                return Ok(new { success = true, message = $"Request {payload.RequestNo} berhasil disetujui, diteruskan ke Gudang!" });
            }
            catch (Exception ex)
            {
                return StatusCode(500, new { success = false, message = ex.Message });
            }
        }

        // 3. ROLE GUDANG: Melihat Daftar Dokumen Siap Cetak
        [HttpGet("request-print/waiting-gudang")]
        public async Task<IActionResult> GetApprovedRequestsForGudang()
        {
            using var conn = new NpgsqlConnection(_config.GetConnectionString("DefaultConnection"));
            await conn.OpenAsync();

            try
            {
                var sql = @"
                    SELECT 
                        h.request_no AS RequestNo,
                        h.po_status AS Status,
                        h.requested_by AS RequestedBy,
                        h.created_at AS CreatedAt,
                        d.id AS Id,
                        d.barcode AS Barcode,
                        d.qty AS Qty,
                        p.description AS Description,
                        p.size AS Size, 
                        p.color AS Color, 
                        p.price AS Price,
                        p.asal_po AS AsalPo,   
                        p.nama_vendor AS NamaVendor
                    FROM request_print_headers h
                    INNER JOIN request_print_details d ON h.request_no = d.request_no
                    LEFT JOIN (
                        SELECT DISTINCT ON (det.barcode) 
                            det.barcode, det.description, det.size, det.color, det.price,
                            hd.po_no AS asal_po, hd.vendor AS nama_vendor
                        FROM po_details det
                        LEFT JOIN po_headers hd ON hd.id = det.po_id AND hd.status = 1
                        WHERE det.status = 1
                        ORDER BY det.barcode, det.id DESC
                    ) p ON p.barcode = d.barcode
                    WHERE h.po_status = 'APPROVED'
                    ORDER BY h.created_at DESC";

                var rawItems = await conn.QueryAsync<RawQueueItem>(sql);

                var groupedData = rawItems
                    .GroupBy(x => x.RequestNo)
                    .Select(g => {
                        var firstItem = g.First();
                        return new {
                            RequestNumber = g.Key,
                            RequestedBy = firstItem.RequestedBy,
                            CreatedAt = firstItem.CreatedAt,
                            TotalItems = g.Sum(x => x.Qty),
                            Items = g.Select(x => new {
                                Id = x.Id,
                                Barcode = x.Barcode,
                                Qty = x.Qty,
                                Description = x.Description,
                                Size = x.Size,
                                Color = x.Color,
                                Price = x.Price,
                                AsalPo = x.AsalPo,
                                NamaVendor = x.NamaVendor
                            }).ToList()
                        };
                    }).ToList();

                return Ok(new { success = true, data = groupedData });
            }
            catch (Exception ex)
            {
                return StatusCode(500, new { success = false, message = ex.Message });
            }
        }

        // REJECT: Area Manager menolak seluruh dokumen
        [HttpPost("request-print/reject")]
        public async Task<IActionResult> RejectDocument([FromBody] AmRejectDto dto)
        {
            if (dto == null || string.IsNullOrEmpty(dto.RequestNo))
                return BadRequest(new { success = false, message = "Nomor request tidak valid." });

            using var conn = new NpgsqlConnection(_config.GetConnectionString("DefaultConnection"));
            await conn.OpenAsync();
            using var tx = await conn.BeginTransactionAsync();

            try
            {
                // PROTEKSI: Tambahkan FOR UPDATE untuk mengunci baris data selama pengecekan status demi keamanan Race Condition
                var status = await conn.QueryFirstOrDefaultAsync<string>(
                    "SELECT po_status FROM request_print_headers WHERE request_no = @RequestNo FOR UPDATE", 
                    new { dto.RequestNo }, tx);

                if (status == null) 
                    return NotFound(new { success = false, message = "Dokumen tidak ditemukan." });

                var allowedStatuses = new[] { "PENDING_AM", "REVISED" };
                if (!allowedStatuses.Contains(status))
                {
                    return BadRequest(new { 
                        success = false, 
                        message = $"Gagal memproses. Dokumen tidak dapat ditolak karena status saat ini sudah {status}." 
                    });
                }

                string updateSql = @"
                    UPDATE request_print_headers 
                    SET po_status = 'REJECTED', 
                        updated_at = NOW() 
                    WHERE request_no = @RequestNo";

                await conn.ExecuteAsync(updateSql, new { dto.RequestNo }, tx);
                await tx.CommitAsync();

                return Ok(new { success = true, message = $"Dokumen {dto.RequestNo} berhasil ditolak." });
            }
            catch (Exception ex)
            {
                await tx.RollbackAsync();
                return StatusCode(500, new { success = false, message = ex.Message });
            }
        }

        // REVISION: Area Manager meminta revisi pada item tertentu
        [HttpPost("request-print/revision")]
        public async Task<IActionResult> SubmitRevision([FromBody] RevisionPayload payload)
        {
            if (payload == null || string.IsNullOrEmpty(payload.RequestNo) || payload.Items == null || !payload.Items.Any())
                return BadRequest(new { success = false, message = "Data revisi tidak valid." });

            using var conn = new NpgsqlConnection(_config.GetConnectionString("DefaultConnection"));
            await conn.OpenAsync();
            using var tx = await conn.BeginTransactionAsync();

            try
            {
                // 1. Update status header menjadi REVISED
                var updateHeaderSql = @"
                    UPDATE request_print_headers 
                    SET po_status = 'REVISED', updated_at = NOW() 
                    WHERE request_no = @RequestNo";
                
                await conn.ExecuteAsync(updateHeaderSql, new { payload.RequestNo }, tx);

                // 2. Update notes_am untuk item-item yang direvisi
                // Menggunakan parameter Dapper untuk mencegah SQL Injection
                var updateDetailSql = @"
                    UPDATE request_print_details 
                    SET notes_am = @NotesAm 
                    WHERE id = @Id AND request_no = @RequestNo";

                foreach (var item in payload.Items)
                {
                    await conn.ExecuteAsync(updateDetailSql, new { 
                        NotesAm = item.NotesAm, 
                        Id = item.Id,
                        RequestNo = payload.RequestNo 
                    }, tx);
                }

                await tx.CommitAsync();
                return Ok(new { success = true, message = "Catatan revisi berhasil disimpan." });
            }
            catch (Exception ex)
            {
                await tx.RollbackAsync();
                return StatusCode(500, new { success = false, message = "Gagal memproses revisi.", error = ex.Message });
            }
        }

        // GENERAL ENDPOINT: Riwayat Pengajuan (Dengan Filter, Pencarian & Pagination)
        [HttpGet("request-print/history")]
        public async Task<IActionResult> GetRequestHistory()
        {
            using var conn = new NpgsqlConnection(_config.GetConnectionString("DefaultConnection"));
            await conn.OpenAsync();

            try
            {
                var sql = @"
                    SELECT 
                        h.request_no AS RequestNo, 
                        h.requested_by AS RequestedBy, 
                        COALESCE(h.updated_at, h.created_at) AS WaktuDiupdate, 
                        h.po_status AS Status,
                        d.qty AS Qty
                    FROM request_print_headers h
                    INNER JOIN request_print_details d ON h.request_no = d.request_no
                    ORDER BY WaktuDiupdate DESC";

                var rawItems = await conn.QueryAsync<dynamic>(sql);
                
                var grouped = rawItems.GroupBy(x => x.requestno).Select(g => new {
                    // DISESUAIKAN: Mengikuti properti snake_case yang dicari oleh frontend Anda
                    request_no = g.Key,
                    requested_by = g.First().requestedby,
                    status = g.First().status, 
                    updated_at = g.First().waktudiupdate, // agar dibaca oleh hist.updated_at
                    
                    // Properti tambahan penyeimbang pola gudang
                    totalitems = g.Sum(i => (int)i.qty),
                    items = Array.Empty<object>() 
                });

                return Ok(new { success = true, data = grouped });
            }
            catch (Exception ex)
            {
                return StatusCode(500, new { success = false, message = "Gagal mengambil data riwayat.", error = ex.Message });
            }
        }
    }
}
