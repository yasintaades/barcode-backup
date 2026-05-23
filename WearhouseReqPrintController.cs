using Microsoft.AspNetCore.Mvc;
using Npgsql;
using Dapper;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Serialization;
using System.Threading.Tasks;
using Microsoft.Extensions.Configuration;
using static Barcode.Api.Controllers.RequestPrintController;

namespace Barcode.Api.Controllers
{
    [ApiController]
    [Route("api/barcode/warehouse")]
    public class WarehouseRequestPrintController : ControllerBase
    {
        private readonly IConfiguration _config;
        
        public WarehouseRequestPrintController(IConfiguration config)
        {
            _config = config;
        }

        // --- DATA TRANSFER OBJECTS (DTO) ---
        public class WarehousePayload 
        { 
            [JsonPropertyName("request_no")] 
            public string RequestNo { get; set; } = string.Empty; 
        }

        public class RawQueryRow
        {
            public string RequestNo { get; set; } = string.Empty;
            public string RequestedBy { get; set; } = string.Empty;
            public DateTime CreatedAt { get; set; }
            public string Status { get; set; } = string.Empty;
            public string Barcode { get; set; } = string.Empty;
            public int Qty { get; set; }
            public string Description { get; set; } = string.Empty;
            public string NamaVendor { get; set; } = string.Empty;
        }

        // =========================================================================
        // GET LIST: Antrean Kerja Aktif / Dokumen Siap Cetak (Status: APPROVED)
        // =========================================================================
        [HttpGet("request-print/waiting-gudang")]
        public async Task<IActionResult> GetReadyToPrint()
        {
            using var conn = new NpgsqlConnection(_config.GetConnectionString("DefaultConnection"));
            
            var sql = @"
                SELECT 
                    h.request_no AS RequestNo, 
                    h.requested_by AS RequestedBy, 
                    h.created_at AS CreatedAt,
                    d.barcode AS Barcode, 
                    d.qty AS Qty, 
                    p.description AS Description, 
                    p.nama_vendor AS NamaVendor
                FROM request_print_headers h
                INNER JOIN request_print_details d ON h.request_no = d.request_no
                LEFT JOIN (
                    SELECT DISTINCT ON (det.barcode) det.barcode, det.description, hd.vendor AS nama_vendor
                    FROM po_details det
                    LEFT JOIN po_headers hd ON hd.id = det.po_id
                    WHERE det.status = 1 
                    ORDER BY det.barcode, det.id DESC
                ) p ON p.barcode = d.barcode
                WHERE h.po_status = 'APPROVED'
                ORDER BY h.created_at DESC";

            try
            {
                var rawItems = await conn.QueryAsync<RawQueryRow>(sql);
                
                var grouped = rawItems.GroupBy(x => x.RequestNo).Select(g => new {
                    // Gunakan camelCase agar sesuai standar frontend
                    requestNumber = g.Key,
                    requestedBy = g.First().RequestedBy ?? "System",
                    createdAt = g.First().CreatedAt,
                    // Tambahkan totalItems agar data summary di tabel/card muncul
                    totalItems = g.Sum(i => i.Qty), 
                    items = g.Select(i => new { 
                        barcode = i.Barcode, 
                        qty = i.Qty,         // Pastikan frontend memanggil "qty"
                        description = i.Description ?? "No Description", 
                        namaVendor = i.NamaVendor ?? "-" // Gunakan camelCase
                    }).ToList()
                });

                return Ok(new { success = true, data = grouped });
            }
            catch (Exception ex)
            {
                return StatusCode(500, new { success = false, message = "Gagal memuat antrean aktif gudang.", error = ex.Message });
            }
        }

        // =========================================================================
        // ACTION PRINT: Memindahkan status dokumen dari APPROVED -> WAITING (Spooler)
        // =========================================================================
        [HttpPost("request-print/queue-to-print-bulk")]
        public async Task<IActionResult> QueueToPrint([FromBody] ApproveBulkPayload payload)
        {
            if (string.IsNullOrEmpty(payload.RequestNo))
            {
                return BadRequest(new { success = false, message = "Parameter request_no tidak boleh kosong." });
            }

            using var conn = new NpgsqlConnection(_config.GetConnectionString("DefaultConnection"));
            
            var sql = @"
                UPDATE request_print_headers 
                SET po_status = 'WAITING', updated_at = NOW() 
                WHERE request_no = @RequestNo AND po_status = 'APPROVED'";

            try
            {
                var rows = await conn.ExecuteAsync(sql, payload);

                if (rows == 0) 
                {
                    return BadRequest(new { success = false, message = "Dokumen tidak ditemukan atau status sudah berubah." });
                }
                
                return Ok(new { success = true, message = $"Dokumen {payload.RequestNo} berhasil masuk ke antrean printer gudang." });
            }
            catch (Exception ex)
            {
                return StatusCode(500, new { success = false, message = "Gagal memproses antrean cetak.", error = ex.Message });
            }
        }

        // =========================================================================
        // GET LIST: Riwayat Cetak Selesai / Log Gudang (Status: WAITING)
        // =========================================================================
        [HttpGet("request-print/history-gudang")]
        public async Task<IActionResult> GetPrintHistory()
        {
            using var conn = new NpgsqlConnection(_config.GetConnectionString("DefaultConnection"));
            
            // COALESCE digunakan untuk mengantisipasi jika updated_at bernilai null di database
            var sql = @"
                SELECT 
                    h.request_no AS RequestNo, 
                    h.requested_by AS RequestedBy, 
                    COALESCE(h.updated_at, h.created_at) AS CreatedAt, 
                    h.po_status AS Status,
                    COALESCE(d.qty, 0) AS Qty
                FROM request_print_headers h
                INNER JOIN request_print_details d ON h.request_no = d.request_no
                WHERE h.po_status = 'WAITING'
                ORDER BY h.updated_at DESC NULLS LAST";

            try
            {
                // Menggunakan Strongly-Typed <RawQueryRow> untuk mencegah crash runtime binder npgsql
                var rawItems = await conn.QueryAsync<RawQueryRow>(sql);
                
                var grouped = rawItems.GroupBy(x => x.RequestNo).Select(g => new {
                    RequestNumber = g.Key,
                    RequestedBy = g.First().RequestedBy ?? "System",
                    CreatedAt = g.First().CreatedAt,
                    Status = "Printed / Sent", 
                    TotalItems = g.Sum(i => i.Qty),
                    Items = Array.Empty<object>() // Riwayat diperingan tanpa membawa struktur item detail
                });

                return Ok(new { success = true, data = grouped });
            }
            catch (Exception ex)
            {
                // Mengembalikan struktur JSON valid (bukan teks error mentah) saat terjadi kendala internal
                return StatusCode(500, new { success = false, message = "Gagal memuat log riwayat cetak gudang.", error = ex.Message });
            }
        }
    }
}
