using CSharPN.Core;
using FluentAssertions;
using Xunit;

namespace CSharPN.Core.Tests;

public class SimulatorTests
{
    // ── Producer-Consumer net ─────────────────────────────────────────────────

    /// <summary>
    /// Simple bounded-buffer producer-consumer:
    ///   Produce: Empty → Item  (produces one token if capacity available)
    ///   Consume: Item  → Empty (consumes one item)
    /// Buffer size = 3.
    /// </summary>
    private class ProducerConsumer : CpnModel
    {
        public readonly Place<int> Buffer;
        public readonly Place<int> Capacity;
        public readonly Transition Produce;
        public readonly Transition Consume;

        private int _nextItem = 1;

        public ProducerConsumer(int bufferSize = 3)
        {
            Buffer = AddPlace<int>("Buffer");
            Capacity = AddPlace("Capacity", Multiset.Repeat(0, bufferSize));

            var slot = new Var<int>("slot");
            var item = new Var<int>("item");

            Produce = AddTransition("Produce")
                .Input(Capacity, slot)       // consume a capacity slot
                .Output(Buffer, () => _nextItem++, "nextItem++")  // produce next item
                .Build();

            Consume = AddTransition("Consume")
                .Input(Buffer, item)
                .Output(Capacity, () => Multiset.Of(0))  // return capacity
                .Build();
        }
    }

    [Fact]
    public void Simulator_fires_transitions_and_changes_marking()
    {
        var model = new ProducerConsumer(bufferSize: 2);
        var sim = new CpnSimulator(model);

        sim.Step(); // Produce
        (model.Buffer.Marking.TotalCount + model.Capacity.Marking.TotalCount).Should().Be(2);
    }

    [Fact]
    public void Run_does_not_exceed_MaxSteps()
    {
        var model = new ProducerConsumer(bufferSize: 3);
        var sim = new CpnSimulator(model);
        var result = sim.Run(new SimulationOptions { MaxSteps = 20 });
        result.Steps.Should().BeLessThanOrEqualTo(20);
    }

    [Fact]
    public void TransitionFired_event_is_raised_for_each_step()
    {
        var model = new ProducerConsumer(bufferSize: 3);
        var sim = new CpnSimulator(model);
        var events = new List<TransitionFiredEventArgs>();
        sim.TransitionFired += (_, e) => events.Add(e);

        sim.Run(new SimulationOptions { MaxSteps = 10 });

        events.Count.Should().Be(10);
        events.Select(e => e.StepNumber).Should().BeInAscendingOrder();
    }

    // ── Deadlock detection ────────────────────────────────────────────────────

    private class DeadlockNet : CpnModel
    {
        public readonly Place<int> P;
        public readonly Transition T;

        public DeadlockNet()
        {
            P = AddPlace("P", Multiset.Of(1)); // exactly one token
            var x = new Var<int>("x");
            T = AddTransition("Consume")
                .Input(P, x)
                .Build(); // no outputs → deadlock after firing
        }
    }

    [Fact]
    public void Run_detects_deadlock_and_reports_it()
    {
        var model = new DeadlockNet();
        var sim = new CpnSimulator(model);

        var result = sim.Run(new SimulationOptions { MaxSteps = 100 });

        result.IsDeadlock.Should().BeTrue();
        result.Steps.Should().Be(1); // fires once, then deadlocks
    }

    [Fact]
    public void DeadlockReached_event_is_raised()
    {
        var model = new DeadlockNet();
        var sim = new CpnSimulator(model);
        bool raised = false;
        sim.DeadlockReached += (_, _) => raised = true;

        sim.Run();

        raised.Should().BeTrue();
    }

    // ── Reset ─────────────────────────────────────────────────────────────────

    [Fact]
    public void Reset_restores_initial_marking()
    {
        var model = new DeadlockNet();
        var sim = new CpnSimulator(model);
        var initial = model.GetState();

        sim.Run();
        model.GetState().Should().NotBe(initial);

        model.Reset();
        model.GetState().Should().Be(initial);
    }

    // ── Deterministic stepping ────────────────────────────────────────────────

    [Fact]
    public void Deterministic_step_fires_chosen_binding()
    {
        var model = new ProducerConsumer(bufferSize: 3);
        var sim = new CpnSimulator(model);

        var produce = model.Produce;
        var binding = produce.GetEnabledBindings().First();

        sim.Step(produce, binding);

        model.Buffer.Marking.TotalCount.Should().Be(1);
    }

    // ── CpnState equality ─────────────────────────────────────────────────────

    [Fact]
    public void States_with_same_marking_are_equal()
    {
        var m1 = new DeadlockNet();
        var m2 = new DeadlockNet();
        m1.GetState().Should().Be(m2.GetState());
    }

    [Fact]
    public void States_differ_after_firing()
    {
        var model = new DeadlockNet();
        var before = model.GetState();
        var sim = new CpnSimulator(model);
        sim.Step();
        model.GetState().Should().NotBe(before);
    }
}
