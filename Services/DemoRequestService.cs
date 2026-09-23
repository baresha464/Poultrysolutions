using System.Collections.Concurrent;
using AmrPoultryFarmWeb.Data;
using AmrPoultryFarmWeb.Models;
using AmrPoultryFarmWeb.Services.Notifications;
using Microsoft.EntityFrameworkCore;

namespace AmrPoultryFarmWeb.Services;

/// <summary>"Request a demo" leads from the public landing page: saved for the Super Admin to follow
/// up. The form is anonymous, so submissions are validated, capped per phone number, and the page
/// adds a hidden honeypot field to turn away simple bots.</summary>
public class DemoRequestService
{
    private readonly IDbContextFactory<FarmDbContext> dbFactory;
    private readonly ILogger<DemoRequestService> log;

    // Same number can't submit more than 3 times in 24 h (in-memory; resets on restart — good enough).
    private static readonly ConcurrentDictionary<string, List<DateTime>> RecentByPhone = new();

    public DemoRequestService(IDbContextFactory<FarmDbContext> dbFactory, ILogger<DemoRequestService> log)
    {
        this.dbFactory = dbFactory;
        this.log = log;
    }

    public enum SubmitResult { Saved, Invalid, TooMany }

    public async Task<SubmitResult> SubmitAsync(DemoRequest input)
    {
        var phone = WhatsAppText.NormalizeNumber(input.Phone);
        var name = (input.Name ?? "").Trim();
        if (phone is null || name.Length < 2) return SubmitResult.Invalid;

        var now = DateTime.UtcNow;
        var times = RecentByPhone.GetOrAdd(phone, _ => new List<DateTime>());
        lock (times)
        {
            times.RemoveAll(t => t < now.AddDays(-1));
            if (times.Count >= 3) return SubmitResult.TooMany;
            times.Add(now);
        }

        using var db = dbFactory.CreateDbContext();
        db.DemoRequests.Add(new DemoRequest
        {
            Name = Cut(name, 80),
            Phone = phone,
            Location = Cut(input.Location, 100),
            Sheds = input.Sheds is > 0 and < 1000 ? input.Sheds : null,
            BirdsPerBatch = input.BirdsPerBatch is > 0 and < 10_000_000 ? input.BirdsPerBatch : null,
            Integrator = Cut(input.Integrator, 80),
            Message = Cut(input.Message, 500),
            Language = AppLanguages.Normalize(input.Language),
            CreatedAtUtc = now,
        });
        await db.SaveChangesAsync();
        log.LogInformation("New demo request from {Name} ({Phone}).", name, WhatsAppText.Mask(phone));
        return SubmitResult.Saved;
    }

    public async Task<List<DemoRequest>> GetAsync(DemoRequestStatus? status = null)
    {
        using var db = dbFactory.CreateDbContext();
        var q = db.DemoRequests.AsNoTracking();
        if (status is not null) q = q.Where(r => r.Status == status);
        return await q.OrderByDescending(r => r.CreatedAtUtc).Take(500).ToListAsync();
    }

    public async Task<int> CountNewAsync()
    {
        using var db = dbFactory.CreateDbContext();
        return await db.DemoRequests.CountAsync(r => r.Status == DemoRequestStatus.New);
    }

    public async Task UpdateAsync(int id, DemoRequestStatus status, string notes)
    {
        using var db = dbFactory.CreateDbContext();
        var r = await db.DemoRequests.FirstOrDefaultAsync(x => x.Id == id);
        if (r is null) return;
        r.Status = status;
        r.AdminNotes = Cut(notes, 500);
        await db.SaveChangesAsync();
    }

    private static string Cut(string? s, int max)
    {
        s = (s ?? "").Trim();
        return s.Length <= max ? s : s[..max];
    }
}
