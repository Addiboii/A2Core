using System.Security.Cryptography;
using AionClientProbe;

// Inventory of one client install: path, size and SHA-1 of every .dat/.ini (others: size only).
// Run it on two installs and compare the TSVs (tools/client_table_diff.py --inventory old.tsv new.tsv).
if (args.Length != 5)
    throw new ArgumentException("ClientDiff <Paks> <key-report> <oodle-dll> <usmap> <output.tsv under research/local/>");
var local = Path.GetFullPath("research/local") + Path.DirectorySeparatorChar;
var output = Path.GetFullPath(args[4]);
if (!output.StartsWith(local, StringComparison.OrdinalIgnoreCase))
    throw new ArgumentException("Write the inventory under research/local/");
using var session = AionClientSession.Open(args[0], args[1], args[2], args[3]);
using var writer = new StreamWriter(output);
foreach (var file in session.Provider.Files.Values.DistinctBy(f => f.Path).OrderBy(f => f.Path, StringComparer.Ordinal))
{
    var hashed = file.Path.EndsWith(".dat", StringComparison.OrdinalIgnoreCase) || file.Path.EndsWith(".ini", StringComparison.OrdinalIgnoreCase);
    string hash;
    try { hash = hashed ? Convert.ToHexString(SHA1.HashData(file.Read())) : "-"; }
    catch (Exception e) { hash = "ERR:" + e.GetType().Name; }
    writer.WriteLine($"{file.Path}\t{file.Size}\t{hash}");
}