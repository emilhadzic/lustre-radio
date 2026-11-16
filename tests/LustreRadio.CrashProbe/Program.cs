using LustreRadio.Data;
using LustreRadio.Playout;

// Proba pada procesa (plan faze B, B-221 i B-222). Pisar ne radi; pesme "sviraju" (zapisi idu u crnu kutiju);
// zatim proces pada (Environment.FailFast), bez ikakvog zatvaranja. Test posle toga pokrece oporavak.
// Upotreba: LustreRadio.CrashProbe <folder> <broj pesama> [kapacitet]
var root = args[0];
var plays = int.Parse(args[1], System.Globalization.CultureInfo.InvariantCulture);
var capacity = args.Length > 2 ? int.Parse(args[2], System.Globalization.CultureInfo.InvariantCulture) : LustreRadio.Core.History.BlackBox.DefaultCapacity;
var db = LustreDatabase.Open(Path.Combine(root, "lustre.db"));
var history = PlaybackHistory.Open(root, db, options: new PlaybackHistoryOptions { Capacity = capacity, StartScribe = false });
for (var i = 0; i < plays; i++)
{
    var pid = Guid.CreateVersion7();
    history.Prepared(pid, 1000 + i, 1, 1, 1, "Proba");
    history.Started(pid, 1000 + i, i * 1000L);
    if (i < plays - 1)
    {
        history.Ended(pid, 1000 + i, (i * 1000L) + 999, 999);
    }
}

Console.WriteLine($"PROBA: {plays} pesama, upisano do {history.Box.Reserved}, izgubljeno {history.Box.LostCount}; pad.");
Console.Out.Flush();
Environment.FailFast("LustreRadio.CrashProbe: namerni pad procesa (test)");
