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
                    position: body.Position, clearPosition: body.ClearPosition ?? false);
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
    }

    public record UpdateBody(string? Label, string? Color, string? Icon, GridPosition? Position, bool? ClearPosition);
    public record GridBody(int Cols, int Rows);
    public record LayoutPlacement(string Id, GridPosition? Position);
    public record LayoutBody(List<LayoutPlacement> Placements);
}
