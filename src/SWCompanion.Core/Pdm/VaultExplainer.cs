using SWCompanion.Core.Checkup;
using SWCompanion.Core.Explain;

namespace SWCompanion.Core.Pdm;

/// <summary>
/// Turns what was found about a vault's servers into checkup rows and plain-English
/// explanations. Names facts plainly and says "likely" for interpretations.
/// </summary>
public static class VaultExplainer
{
    public const string Group = "PDM client";
    public const string FqdnSource = "SOLIDWORKS knowledge base S-038755, \"Configure SOLIDWORKS PDM for FQDN\"";
    public const string Source = "SOLIDWORKS PDM architecture; Windows networking";

    /// <summary>Connect times above these are worth pointing out.</summary>
    public const double SlowMs = 15, VerySlowMs = 50;

    /// <summary>Example number of database requests in one save, used only to show how delays add up.</summary>
    public const int ExampleRequests = 400;

    public static IReadOnlyList<CheckResult> Checks(
        VaultView v,
        IReadOnlyDictionary<string, NameLookup> lookups,
        IReadOnlyList<HostsEntry> hosts,
        IReadOnlyList<PortTest> tests)
    {
        var rows = new List<CheckResult> { Servers(v, lookups), Copies(v), Names(v, lookups), Hosts(v, hosts) };
        rows.AddRange(Ports(v, tests));
        return rows;
    }

    private static CheckResult Servers(VaultView v, IReadOnlyDictionary<string, NameLookup> lookups)
    {
        string id = $"vault-servers:{v.Name}";
        const string plain = Analogies.Lead + "the database server is the vault's catalogue desk (who has what, data cards, " +
                             "references); the archive server is the warehouse holding the files themselves. They can be in the " +
                             "same building or different ones, and every trip to either takes time.";
        if (v.DatabaseServer is null || v.ArchiveServer is null)
            return new(id, $"vault {v.Name}: servers", CheckStatus.Unknown, "The vault view's registry entry doesn't list both servers.", "", Source, plain, Group);

        bool same = v.SameServerByName || SharesAddress(v, lookups);
        string sql = v.SqlInstance is null ? v.DatabaseHost! : $"{v.DatabaseHost} (SQL instance {v.SqlInstance})";
        string finding = same
            ? $"The database (SQL) and the archive are on the same server: {v.ArchiveServer}. Archive port {v.ArchivePortOrDefault}."
            : $"The database (SQL) is on {sql}; the archive is on {v.ArchiveServer}, port {v.ArchivePortOrDefault}. " +
              "With two servers, the delay to each one adds up separately.";
        if (v.LocalFolder is not null) finding += $" Local vault view folder: {v.LocalFolder}.";
        return new(id, $"vault {v.Name}: servers", CheckStatus.Good, finding, "", Source, plain, Group);
    }

    private static bool SharesAddress(VaultView v, IReadOnlyDictionary<string, NameLookup> lookups) =>
        v.ArchiveServer is not null && v.DatabaseHost is not null &&
        lookups.TryGetValue(v.ArchiveServer, out var a) && lookups.TryGetValue(v.DatabaseHost, out var d) &&
        a.Addresses.Intersect(d.Addresses).Any();

    private static CheckResult Copies(VaultView v)
    {
        string id = $"vault-copies:{v.Name}";
        const string plain = Analogies.Lead + "Windows keeps two copies of the vault's address card, one for 64-bit and one for " +
                             "32-bit programs. If they disagree, some programs go to the old address.";
        if (v.CopiesDisagree.Count == 0)
            return new(id, $"vault {v.Name}: settings copies", CheckStatus.Good, "Both registry copies of the vault view settings agree.", "", FqdnSource, plain, Group);
        return new(id, $"vault {v.Name}: settings copies", CheckStatus.Look,
            "The 64-bit and 32-bit registry copies disagree: " + string.Join("; ", v.CopiesDisagree) + ".",
            @"Update both HKEY_LOCAL_MACHINE\Software\SolidWorks\Applications\PDMWorks Enterprise\Databases\<vault> and the same " +
            @"path under Software\WOW6432Node so DbServer and ServerLoc match. Exit the PDM client from the system tray first. " +
            "Back up the registry before changing it.", FqdnSource, plain, Group);
    }

    private static CheckResult Names(VaultView v, IReadOnlyDictionary<string, NameLookup> lookups)
    {
        string id = $"vault-names:{v.Name}";
        const string plain = Analogies.Lead + "a server name is like a company name in the phone book. A short name (\"pdmserver\") " +
                             "relies on Windows guessing which phone book to look in; a full name (\"pdmserver.company.local\") " +
                             "can only mean one place.";
        var names = new[] { v.ArchiveServer, v.DatabaseHost }.Where(n => n is not null).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        var failed = names.Where(n => lookups.TryGetValue(n!, out var l) && l.Error is not null).ToList();
        if (failed.Count > 0)
            return new(id, $"vault {v.Name}: server names", CheckStatus.Problem,
                string.Join(" ", failed.Select(n => $"\"{n}\": {lookups[n!].Error}.")) + " PDM can't reach a server it can't find.",
                "Check the PC is on the company network (or VPN), then run \"nslookup <server name>\" in a command prompt. If the " +
                "name isn't found, the DNS setup needs fixing, or the vault view needs the server's correct full name.",
                FqdnSource, plain, Group);

        var found = names.Select(n => lookups.TryGetValue(n!, out var l) ? $"{n} = {string.Join(", ", l.Addresses)}" : $"{n} (not looked up)");
        bool allFull = names.All(n => lookups.TryGetValue(n!, out var l) && (l.IsFullName || l.Addresses.Count > 0 && System.Net.IPAddress.TryParse(n, out _)));
        return new(id, $"vault {v.Name}: server names", CheckStatus.Good,
            "Every server name resolves: " + string.Join("; ", found) + "." +
            (allFull ? "" : " Short names are used; that works when Windows' DNS suffix is set. Full names (FQDN) are an optional, more exact setup."),
            "", FqdnSource, plain, Group);
    }

    private static CheckResult Hosts(VaultView v, IReadOnlyList<HostsEntry> hosts)
    {
        string id = $"vault-hosts:{v.Name}";
        const string plain = Analogies.Lead + "the hosts file is a sticky note on the PC saying \"this name is at this address\". " +
                             "Windows believes the sticky note over the phone book, so if the server ever moves, the note sends PDM " +
                             "to the old address.";
        var vaultNames = new[] { v.ArchiveServer, v.DatabaseHost }.Where(n => n is not null).Select(n => VaultView.ShortName(n!)).ToList();
        var pinned = hosts.Where(h => vaultNames.Contains(VaultView.ShortName(h.Name), StringComparer.OrdinalIgnoreCase)).ToList();
        const string fix = @"Open C:\Windows\System32\drivers\etc\hosts in Notepad (as administrator) and remove the lines for the PDM " +
                           "servers, so the names come from DNS. Keep a copy of the file first.";
        if (pinned.Count > 0)
            return new(id, $"vault {v.Name}: hosts file", CheckStatus.Look,
                "The hosts file pins " + string.Join("; ", pinned.Select(h => $"{h.Name} to {h.Address}")) +
                ", bypassing DNS. If that server's address changes, PDM will keep going to the old one.", fix, FqdnSource, plain, Group);
        if (hosts.Count > 0)
            return new(id, $"vault {v.Name}: hosts file", CheckStatus.Good,
                $"The hosts file has {hosts.Count} other entr{(hosts.Count == 1 ? "y" : "ies")}, none for the vault's servers. For PDM, " +
                "the setup guide recommends only the localhost line.", "", FqdnSource, plain, Group);
        return new(id, $"vault {v.Name}: hosts file", CheckStatus.Good, "The hosts file only has the usual localhost entries.", "", FqdnSource, plain, Group);
    }

    private static IEnumerable<CheckResult> Ports(VaultView v, IReadOnlyList<PortTest> tests)
    {
        const string plain = Analogies.Lead + "the port test is knocking on the server's door and timing how long until someone " +
                             "says \"yes?\". It doesn't ask for anything; it just measures the trip there and back.";
        foreach (var role in new[] { "archive", "database" })
        {
            string id = $"vault-port-{role}:{v.Name}";
            string title = $"vault {v.Name}: {role} server port";
            var t = tests.LastOrDefault(x => x.Role == role);
            if (role == "database" && v.SqlPort is null && t is null)
            {
                yield return new(id, title, CheckStatus.Unknown,
                    $"The database uses a named SQL Server instance ({v.SqlInstance}), whose port SQL Server chooses, so the app " +
                    "can't test it directly yet.", "", Source, plain, Group);
                continue;
            }
            if (t is null)
            {
                yield return new(id, title, CheckStatus.Unknown,
                    "Not tested. Use the \"test the server ports\" key in the pdm vault section (it only knocks and times the answer).",
                    "", Source, plain, Group);
                continue;
            }
            yield return Describe(id, title, t, plain);
        }
    }

    public static CheckResult Describe(string id, string title, PortTest t, string plain)
    {
        string where = $"{t.Server} port {t.Port}";
        switch (t.Result)
        {
            case PortResult.Open:
                double ms = t.Milliseconds ?? 0;
                string distance = ms < 2 ? "like a server in the same building" : ms < SlowMs ? "a nearby server"
                                : ms < VerySlowMs ? "a distant server or a VPN" : "a far-away server: every request waits this long";
                string example = $"For example, a save that makes {ExampleRequests} small requests to a server this far away waits " +
                                 $"about {ExampleRequests * ms / 1000:0.0} s for the network alone.";
                var status = ms >= VerySlowMs ? CheckStatus.Look : CheckStatus.Good;
                return new(id, title, status, $"{where} answered in {ms:0} ms ({distance}). {example} (tested {t.When:HH:mm})",
                    status == CheckStatus.Good ? "" :
                    "Large delays are usually distance or the link (VPN, internet). Putting the database closer to users, or a " +
                    "replicated archive server at this site, usually helps; your IT team or reseller can advise.",
                    Source, plain, Group);
            case PortResult.Refused:
                return new(id, title, CheckStatus.Problem,
                    $"{where} actively refused the connection (tested {t.When:HH:mm}). The server is reachable, but nothing is " +
                    "accepting connections on that port: the PDM or SQL service may be stopped, or it uses a different port.",
                    "Check the service is running on the server and the port number matches the vault setup.", Source, plain, Group);
            case PortResult.NameNotFound:
                return new(id, title, CheckStatus.Problem, $"{where}: the server name couldn't be found (tested {t.When:HH:mm}).",
                    "See the server names row above.", Source, plain, Group);
            default:
                return new(id, title, CheckStatus.Problem,
                    $"{where} didn't answer within 3 seconds (tested {t.When:HH:mm}). Something is likely blocking it (a firewall " +
                    "or security software on this PC, the network or the server), or the server is off or out of reach (VPN not connected?).",
                    "Check the VPN or office network connection, then ask your IT team whether the firewall allows this port " +
                    "between this PC and the server. The app can see that the knock went unanswered, not which firewall blocked it.",
                    Source, plain, Group);
        }
    }
}
