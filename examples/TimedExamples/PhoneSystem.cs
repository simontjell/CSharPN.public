using CSharPN.Core;

namespace TimedExamples;

// ── PhoneSystem ───────────────────────────────────────────────────────────────
//
// Timed CPN of a telephone exchange (Jensen Vol. 1 §4.2 style).
//
// NumPhones phones share a limited number of lines (LineCapacity).
// Any idle phone can call any other idle phone; the call occupies one line for
// CallDuration time units.  After the call, both phones and the line are freed.
//
// Modelling decisions:
//   • A "call request" is placed at regular intervals from a pre-scheduled source.
//   • Only one request per pair is injected; the system is stochastic in which
//     phone calls which thanks to random binding selection.
//   • Busy phones cannot initiate or receive calls (guard on StartCall).
//
// Colour sets:
//   int           = phone ID (1..NumPhones)
//   Call(A,B)     = an active call between phones A and B
//
// Places:
//   Idle          : int           – phones not on a call
//   Lines         : int           – available line tokens (capacity)
//   Requests      : Timed<int>    – pre-scheduled call-attempt events
//   ActiveCalls   : Timed<Call>   – ongoing calls (ready = call ends)

public sealed class PhoneSystem : TimedCpnModel
{
    // ── Constants ─────────────────────────────────────────────────────────────
    public const int NumPhones      = 6;
    public const int LineCapacity   = 3;   // max simultaneous calls
    public const int CallDuration   = 20;  // time units per call
    public const int RequestInterval = 7;  // new call request every N time units
    public const int NumRequests    = 10;  // total call attempts to schedule

    // ── Colour set ────────────────────────────────────────────────────────────
    public sealed record Call(int PhoneA, int PhoneB);

    // ── Public places ─────────────────────────────────────────────────────────
    public readonly Place<int>         Idle;
    public readonly Place<int>         Lines;
    public readonly Place<Timed<int>>  Requests;
    public readonly Place<Timed<Call>> ActiveCalls;
    public readonly Place<int>         Completed;

    public PhoneSystem()
    {
        // All phones start idle.
        Idle = AddPlace<int>("Idle",
            Enumerable.Range(1, NumPhones)
                      .Aggregate(Multiset<int>.Empty, (m, i) => m.Add(i, 1)));

        // Lines available.
        Lines = AddPlace<int>("Lines",
            Enumerable.Range(1, LineCapacity)
                      .Aggregate(Multiset<int>.Empty, (m, i) => m.Add(i, 1)));

        // Pre-schedule call requests: each is a trigger event (value = ignored).
        var reqs = Enumerable.Range(0, NumRequests)
            .Aggregate(Multiset<Timed<int>>.Empty,
                (m, i) => m.Add(Timed<int>.At(i + 1, i * RequestInterval), 1));
        Requests    = AddTimedPlace<int>("Requests", reqs);
        ActiveCalls = AddTimedPlace<Call>("ActiveCalls");
        Completed   = AddPlace<int>("Completed");

        // ── Variables ─────────────────────────────────────────────────────────
        var caller = new Var<int>("caller");
        var recv   = new Var<int>("receiver");
        var req    = new Var<int>("req");      // request trigger (dummy value)
        var line   = new Var<int>("line");
        var call   = new Var<Call>("call");

        // ── Transitions ───────────────────────────────────────────────────────

        // StartCall: a request arrives → two distinct idle phones connect on a free line.
        AddTransition("StartCall")
            .TimedInput(Requests, req)
            .Input(Idle, caller)
            .Input(Idle, recv)
            .Input(Lines, line)
            .Guard(() => caller.Val != recv.Val)
            .TimedOutput(ActiveCalls, () => new Call(caller.Val, recv.Val), CallDuration)
            .Build();

        // EndCall: call duration expires → both phones become idle, line released.
        AddTransition("EndCall")
            .TimedInput(ActiveCalls, call)
            .Output(Idle, () => call.Val.PhoneA)
            .Output(Idle, () => call.Val.PhoneB)
            .Output(Lines, () => 1)
            .Output(Completed, () => 1)
            .Build();
    }

    // ── Result inspection ──────────────────────────────────────────────────────
    public int ActiveCallCount    => ActiveCalls.Marking.TotalCount;
    public int IdlePhoneCount     => Idle.Marking.TotalCount;
    public int CompletedCallCount => Completed.Marking.TotalCount;

    public string Summary()
        => $"Idle={IdlePhoneCount}/{NumPhones}  " +
           $"Active={ActiveCallCount}  " +
           $"Completed={CompletedCallCount}";
}
