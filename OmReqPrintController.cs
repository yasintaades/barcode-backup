using Microsoft.AspNetCore.Mvc;
using Npgsql;
using Dapper;
using System.Text.Json.Serialization;

namespace Barcode.Api.Controllers
{
    [ApiController]
    [Route("api/barcode/am")]
    public class AmRequestPrintController : ControllerBase
    {
        private readonly IConfiguration _config;
        public AmRequestPrintController(IConfiguration config) => _config = config;

        public class AmPayload { [JsonPropertyName("request_no")] public string RequestNo { get; set; } }

        // ==========================================
        // GET LIST: Ambil daftar pengajuan status PENDING_AM
        // ==========================================
        [HttpGet("request-print/waiting-approval")]
        public async Task<IActionResult> GetWaitingApproval()
        {
            using var conn = new NpgsqlConnection(_config.GetConnectionString("DefaultConnection"));
            var sql = @"
                SELECT 
                    h.request_no AS RequestNo, h.po_status AS Status, h.requested_by AS RequestedBy, h.created_at AS CreatedAt,
                    d.barcode AS Barcode, d.qty AS Qty, p.description AS Description
                FROM request_print_headers h
                INNER JOIN request_print_details d ON h.request_no = d.request_no
                LEFT JOIN (
                    SELECT DISTINCT ON (barcode) barcode, description FROM po_details WHERE status = 1 ORDER BY barcode, id DESC
                ) p ON d.barcode = p.barcode
                WHERE h.po_status = 'PENDING_AM'
                ORDER BY h.created_at DESC";

            var rawItems = await conn.QueryAsync<dynamic>(sql);
            
            var grouped = rawItems.GroupBy(x => x.requestno).Select(g => new {
                RequestNumber = g.Key,
                RequestedBy = g.First().requestedby,
                CreatedAt = g.First().createdat,
                Items = g.Select(i => new { i.barcode, i.qty, i.description })
            });

            return Ok(new { success = true, data = grouped });
        }

        // ==========================================
        // APPROVE: Menyetujui pengajuan toko
        // ==========================================
        [HttpPost("request-print/approve")]
        public async Task<IActionResult> ApproveRequest([FromBody] AmPayload payload)
        {
            using var conn = new NpgsqlConnection(_config.GetConnectionString("DefaultConnection"));
            var rows = await conn.ExecuteAsync(@"
                UPDATE request_print_headers 
                SET po_status = 'APPROVED' 
                WHERE request_no = @RequestNo AND po_status = 'PENDING_AM'", payload);

            if (rows == 0) return BadRequest(new { success = false, message = "Data tidak valid atau sudah diproses." });
            return Ok(new { success = true, message = $"Request {payload.RequestNo} berhasil di-approve." });
        }
    }
}
