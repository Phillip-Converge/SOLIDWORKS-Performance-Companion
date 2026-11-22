namespace SWCompanion.Core.Explain;

/// <summary>
/// Everyday comparisons for the technical terms the app uses, so every answer can
/// end with "Think of it like this". They share one picture, a machine shop, so
/// they stay consistent: the processor's cores are machinists, RAM is the
/// workbench, the page file is the storeroom down the hall, and so on.
/// </summary>
public static class Analogies
{
    public const string Lead = "Think of it like this: ";

    // ---- processor ----
    public const string Cores =
        "your processor is a machine shop and each core is a machinist. A rebuild is a job where every step needs the part " +
        "from the step before, so only one machinist can work on it at a time while the others stand around. Hiring more " +
        "machinists doesn't help that job; a faster machinist does.";

    public const string PerformanceAndEfficiency =
        "the shop has two kinds of staff. Performance cores are the skilled machinists: fast, but they use more power. " +
        "Efficiency cores are apprentices: cheaper to run, slower on hard jobs. Windows is the shop foreman, constantly " +
        "handing jobs out and moving them between workers, which is why the busy bar hops around.";

    public const string HyperThreading =
        "a pair of slots like 00 and 01 is one machinist running two jobs by switching between them. Handy for keeping busy, " +
        "but each job goes slower than if that machinist worked on it alone.";

    public const string MoreCores =
        "more machinists help when a job can be split up, like painting ten separate parts at once (drawing views, rendering, " +
        "simulation). They don't help a job that has to be done in order, like assembling a gearbox one part at a time (rebuilds).";

    public const string Bars =
        "each bar is one worker's workload over the last second: a short bar is someone mostly waiting for work, a full bar is " +
        "someone flat out.";

    // ---- memory ----
    public const string RamAndPageFile =
        "RAM is your workbench: everything you're working on sits there within arm's reach. The page file (pagefile.sys) is a " +
        "storeroom down the hall. When the bench fills up, Windows moves things you haven't touched in a while to the storeroom. " +
        "\"Set aside\" is everything you've reserved space for, on the bench or in the storeroom. Fetching something back from " +
        "the storeroom means a walk down the hall, which is the pause you feel when you switch back to SOLIDWORKS.";

    public const string ObjectLimits =
        "Windows runs a tool crib that lends every program a fixed number of tools. Drawing objects (GDI) are the drawing tools: " +
        "pens, brushes, stencils and stamps used to paint the screen. Window objects (USER) are the parts of the control panel: " +
        "windows, buttons and menus. When a program has borrowed its full allowance, the crib says no, the program can't draw " +
        "its menus any more, and it usually falls over. A bigger bench (more RAM) doesn't change the size of the allowance.";

    public const string MemoryLimits =
        "RAM full is a crowded bench: you can still work, but you keep walking to the storeroom. The total limit is bench plus " +
        "storeroom both full: there's nowhere to put a new part, so programs fail. The object limit is the tool crib refusing " +
        "to lend another tool, even though the bench has room.";

    public const string MoreRam =
        "more RAM is a bigger workbench. It only helps if you keep running out of bench space; if the bench is half empty, a " +
        "bigger one doesn't make you work faster.";

    public const string Registry =
        "the registry is Windows' master settings binder. Changing a page changes how Windows behaves, and a wrong entry can " +
        "cause trouble, which is why you photocopy it (back it up) first.";

    // ---- graphics ----
    public const string GraphicsCard =
        "the graphics card is a specialist illustrator with their own desk, redrawing your model every time you turn it. " +
        "Built-in graphics is the office assistant who can also draw, but more slowly and while borrowing space on the main bench. " +
        "Windows' software fallback is having no illustrator at all, so a machinist (the processor) draws by hand, very slowly.";

    public const string VideoMemory =
        "video memory is the illustrator's own desk, right at their elbow, where the model being drawn is laid out. \"Shared\" " +
        "memory is the illustrator borrowing space on the main workbench across the room: it works, but every trip takes longer.";

    public const string GraphicsBottleneck =
        "if the illustrator is drawing flat out and still can't keep up as you spin the model, the picture stutters. If the " +
        "illustrator is idle while you wait, the hold-up is somewhere else in the shop.";

    // ---- disk and network ----
    public const string DiskOrNetwork =
        "reading from your own SSD is grabbing a part from the shelf next to your bench. Reading from a server or vault is " +
        "ordering it from the warehouse across town. Windows only records how many parts arrived, not which delivery van brought them.";

    public const string DiskBusy =
        "the disk is a parts counter. \"Busy\" is how much of the time someone is standing at the counter. \"Response time\" is how " +
        "long each person waits to be served. A fast counter can be busy all day with no queue; a slow one has a line out the door.";

    public const string SlowDisk =
        "antivirus is a security guard opening every box that comes through the door before you can use it. An old spinning hard " +
        "drive is a parts carousel you have to wait to rotate round, where an SSD is a drawer you just pull open.";

    public const string Network =
        "the network is the road to the warehouse, shared by every delivery: SOLIDWORKS, the vault, browsers, cloud sync and " +
        "updates all use the same road, and Windows only counts the total traffic, not whose trucks they are.";

    // ---- power ----
    public const string PowerPlan =
        "the power plan is the governor on the shop's machines. \"Balanced\" lets Windows hold the machines back to save " +
        "electricity; \"High performance\" takes the governor off so they can run at full speed when you need them.";

    public const string ProcessorCap =
        "the maximum processor state is a speed limiter. Set below 100%, the machine can never reach top speed, and on many " +
        "processors the turbo boost is switched off too.";

    public const string Battery =
        "running on battery is like running the shop from a generator in economy mode: everything slows down to make the fuel last.";

    public const string PageFileSetting =
        "the page file is the storeroom down the hall from your workbench. Letting Windows manage it means Windows makes the " +
        "storeroom bigger when it needs to. The counter shows how full the storeroom is; above about three-quarters full, " +
        "there's a lot of walking back and forth.";

    // ---- freezes ----
    public const string NotResponding =
        "every second the Companion taps SOLIDWORKS on the shoulder and asks \"are you there?\". If it doesn't answer for 2 " +
        "seconds, it's frozen.";

    public const string Waiting =
        "a machinist standing by the phone waiting for a supplier to call back: not busy, just stuck until the call comes.";

    public const string OneCoreBusy =
        "one machinist working through a job step by step while the rest of the shop waits for them.";

    public const string Loading =
        "parts being fetched from the shelves (or the warehouse) before work can start.";

    public const string OpenDialog =
        "a note left on the machinist's bench saying \"call me before you continue\", hidden under a pile of papers.";

    /// <summary>Adds a "Think of it like this" paragraph to an answer.</summary>
    public static string With(string answer, string analogy) =>
        string.IsNullOrWhiteSpace(answer) ? answer : answer + "\n\n" + Lead + analogy;
}
