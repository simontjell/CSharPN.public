using CSharPN.Core;

/// <summary>
/// Classic Readers-Writers problem from Jensen's Coloured Petri Nets literature.
///
/// Multiple readers may access shared data concurrently, but a writer requires
/// exclusive access. The model uses N resource slots: a reader consumes 1 slot,
/// a writer consumes all N slots (preventing any concurrent access).
///
/// Constants:
///   NumReaders = 3   – number of reader processes
///   NumWriters = 2   – number of writer processes
///   N          = 3   – total reader slots (must equal NumReaders for full concurrency)
///
/// Places:
///   FreeReaders – idle reader IDs
///   FreeWriters – idle writer IDs
///   Reading     – readers currently reading
///   Writing     – writer currently writing (at most 1 at a time)
///   Slots       – shared resource slot tokens (N tokens; readers take 1, writers take all)
///
/// Transitions:
///   StartRead   – reader acquires one slot and begins reading
///   StopRead    – reader finishes and returns its slot
///   StartWrite  – writer acquires all N slots (exclusive) and begins writing
///   StopWrite   – writer finishes and returns all N slots
/// </summary>
public class ReadersWriters : CpnModel
{
    public const int NumReaders = 3;
    public const int NumWriters = 2;
    public const int N = 3;   // total slot count; must equal NumReaders

    // Places
    public readonly Place<int> FreeReaders;
    public readonly Place<int> FreeWriters;
    public readonly Place<int> Reading;
    public readonly Place<int> Writing;
    public readonly Place<int> Slots;

    public ReadersWriters() : base("ReadersWriters")
    {
        FreeReaders = AddPlace("Free Readers", Multiset.Of(1, 2, 3));
        FreeWriters = AddPlace("Free Writers", Multiset.Of(1, 2));
        Reading     = AddPlace<int>("Reading");
        Writing     = AddPlace<int>("Writing");
        Slots       = AddPlace("Slots", Multiset.Repeat(0, N));

        // Variables
        var r = new Var<int>("r");
        var w = new Var<int>("w");
        var s = new Var<int>("s");

        // 1. StartRead: reader acquires one slot
        AddTransition("Start Reading")
            .Input(FreeReaders, r)
            .Input(Slots, s)          // consumes one slot token
            .Output(Reading, r)
            .Build();

        // 2. StopRead: reader finishes and returns one slot
        AddTransition("Stop Reading")
            .Input(Reading, r)
            .Output(FreeReaders, () => Multiset.Of(r.Val))
            .Output(Slots, () => Multiset.Of(0))
            .Build();

        // 3. StartWrite: writer acquires ALL N slots (exclusive access)
        AddTransition("Start Writing")
            .Input(FreeWriters, w)
            .Input(Slots, () => Multiset.Repeat(0, N))   // consume all N slots
            .Output(Writing, () => Multiset.Of(w.Val))
            .Build();

        // 4. StopWrite: writer finishes and returns ALL N slots
        AddTransition("Stop Writing")
            .Input(Writing, w)
            .Output(FreeWriters, () => Multiset.Of(w.Val))
            .Output(Slots, () => Multiset.Repeat(0, N))
            .Build();
    }
}
