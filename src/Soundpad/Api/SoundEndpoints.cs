using Soundpad.Api.Dto;
using Soundpad.Models;
using Soundpad.Sound;

namespace Soundpad.Api;

public static class SoundEndpoints
{
    public static void Map(WebApplication app)
    {
        var g = app.MapGroup("/api");

        g.MapPost("/sounds/upload", async (HttpRequest req, SoundLibrary lib, StateHub hub) =>
        {
            // Third defense layer (Kestrel + FormOptions are layers 1 and 2):
            if (req.ContentLength is long len && len > SoundLibrary.MaxUploadBytes)
                return Results.BadRequest(new { error = "too_large" });
            if (!req.HasFormContentType) return Results.BadRequest(new { error = "expected_multipart" });
            var form = await req.ReadFormAsync();
            var file = form.Files.FirstOrDefault();
            if (file is null) return Results.BadRequest(new { error = "missing_file" });
            try
            {
                using var stream = file.OpenReadStream();
                var result = lib.Upload(file.FileName, stream);
                _ = hub.BroadcastAsync("libraryChanged", new { });
                return Results.Created($"/api/sounds/{result.Entry.Id}", result.Entry);
            }
            catch (InvalidOperationException ex)
            {
                return Results.BadRequest(new { error = ex.Message });
            }
        }).DisableAntiforgery();

        g.MapPut("/sounds/{id}", (string id, UpdateBody body, SoundLibrary lib, StateHub hub) =>
        {
            try
            {
                lib.UpdateSound(id,
                    label: body.Label, color: body.Color, icon: body.Icon,
                    position: body.Position, clearPosition: body.ClearPosition ?? false,
                    volume: body.Volume);
                _ = hub.BroadcastAsync("libraryChanged", new { });
                return Results.NoContent();
            }
            catch (InvalidOperationException ex) { return Results.BadRequest(new { error = ex.Message }); }
        });

        g.MapDelete("/sounds/{id}", (string id, bool? deleteFile, SoundLibrary lib, StateHub hub) =>
        {
            try
            {
                lib.DeleteSound(id, deleteFile ?? false);
                _ = hub.BroadcastAsync("libraryChanged", new { });
                return Results.NoContent();
            }
            catch (InvalidOperationException ex) { return Results.BadRequest(new { error = ex.Message }); }
        });

        // Per-sound volume — convenience endpoint so the WPF context menu (and
        // mobile, eventually) can wire a single slider without doing the full
        // PUT-with-every-field dance. Scoped to the active board, like the
        // other sound-mutating endpoints.
        g.MapPost("/sounds/{id}/volume", (string id, VolumeBody body, SoundLibrary lib, StateHub hub) =>
        {
            try
            {
                lib.UpdateSound(id, volume: Math.Clamp(body.Value, 0, 100));
                _ = hub.BroadcastAsync("libraryChanged", new { });
                return Results.NoContent();
            }
            catch (InvalidOperationException ex) { return Results.BadRequest(new { error = ex.Message }); }
        });

        // Move a sound from the active board to another. Used by the WPF
        // context menu "Mover para outro board" submenu. Idempotent if the
        // target is already the active board.
        g.MapPost("/sounds/{id}/move", (string id, MoveBody body, SoundLibrary lib, StateHub hub) =>
        {
            try
            {
                lib.MoveSoundToBoard(id, body.BoardId);
                _ = hub.BroadcastAsync("libraryChanged", new { });
                return Results.NoContent();
            }
            catch (InvalidOperationException ex) { return Results.BadRequest(new { error = ex.Message }); }
        });

        g.MapPost("/grid", (GridBody body, SoundLibrary lib, StateHub hub) =>
        {
            if (body.Cols < 1 || body.Rows < 1) return Results.BadRequest(new { error = "invalid_dimensions" });
            lib.ResizeGrid(body.Cols, body.Rows);
            _ = hub.BroadcastAsync("libraryChanged", new { });
            return Results.NoContent();
        });

        g.MapPost("/grid/layout", (LayoutBody body, SoundLibrary lib, StateHub hub) =>
        {
            try
            {
                lib.ApplyLayout(body.Placements.Select(p => (p.Id, p.Position)));
                _ = hub.BroadcastAsync("libraryChanged", new { });
                return Results.NoContent();
            }
            catch (InvalidOperationException ex) { return Results.BadRequest(new { error = ex.Message }); }
        });

        // ─── boards ──────────────────────────────────────────────────────

        g.MapGet("/boards", (SoundLibrary lib) =>
        {
            var summaries = lib.Config.Boards
                .Select(b => new BoardSummaryDto(b.Id, b.Name, b.Color))
                .ToList();
            return Results.Ok(new
            {
                boards = summaries,
                activeBoardId = lib.Config.ActiveBoardId,
            });
        });

        g.MapPost("/boards", (BoardCreateBody body, SoundLibrary lib, StateHub hub) =>
        {
            try
            {
                var b = lib.CreateBoard(body.Name, body.Color);
                _ = hub.BroadcastAsync("boardsChanged", new { });
                return Results.Created($"/api/boards/{b.Id}", new BoardSummaryDto(b.Id, b.Name, b.Color));
            }
            catch (InvalidOperationException ex) { return Results.BadRequest(new { error = ex.Message }); }
        });

        g.MapPut("/boards/{id}", (string id, BoardUpdateBody body, SoundLibrary lib, StateHub hub) =>
        {
            try
            {
                lib.UpdateBoard(id, body.Name, body.Color);
                _ = hub.BroadcastAsync("boardsChanged", new { });
                return Results.NoContent();
            }
            catch (InvalidOperationException ex) { return Results.BadRequest(new { error = ex.Message }); }
        });

        g.MapDelete("/boards/{id}", (string id, SoundLibrary lib, StateHub hub) =>
        {
            try
            {
                lib.DeleteBoard(id);
                _ = hub.BroadcastAsync("boardsChanged", new { });
                return Results.NoContent();
            }
            catch (InvalidOperationException ex) { return Results.BadRequest(new { error = ex.Message }); }
        });

        g.MapPost("/boards/{id}/activate", async (string id, SoundLibrary lib, StateHub hub, HttpContext http) =>
        {
            try
            {
                lib.ActivateBoard(id);
                // Broadcast a typed event so WS-connected clients (WPF window,
                // mobile, browser) can switch their view without round-tripping
                // /api/state. libraryChanged also fires from Save() so legacy
                // clients still get the update.
                await hub.BroadcastAsync("activeBoardChanged", new { boardId = id }, OriginIdOf(http));
                return Results.NoContent();
            }
            catch (InvalidOperationException ex) { return Results.BadRequest(new { error = ex.Message }); }
        });
    }

    private static string? OriginIdOf(HttpContext ctx)
    {
        var v = ctx.Request.Headers["X-Origin-Id"].FirstOrDefault();
        return string.IsNullOrWhiteSpace(v) ? null : v;
    }

    public record UpdateBody(string? Label, string? Color, string? Icon, GridPosition? Position, bool? ClearPosition, int? Volume);
    public record GridBody(int Cols, int Rows);
    public record LayoutPlacement(string Id, GridPosition? Position);
    public record LayoutBody(List<LayoutPlacement> Placements);
    public record VolumeBody(int Value);
    public record MoveBody(string BoardId);
    public record BoardCreateBody(string Name, string? Color = null);
    public record BoardUpdateBody(string? Name, string? Color);
}
