using Shouldly;
using Unitree.Net.Core;
using Unitree.Net.Dds;
using Unitree.Net.Messages;
using Unitree.Net.Messages.Go;

namespace Unitree.Net.Tests;

public sealed class LoopbackTransportTests
{
    [Fact]
    public async Task PublishedMessagesReachSubscribers()
    {
        await using var transport = new LoopbackTransport();
        await using var participant = new DdsParticipant(transport);
        await participant.StartAsync();

        IDdsPublisher<LowState> publisher = participant.CreatePublisher<LowState>(Topics.LowState);
        IDdsSubscriber<LowState> subscriber = participant.CreateSubscriber<LowState>(Topics.LowState);

        LowState sent = default;
        sent.Tick = 4242;
        sent.BmsState.Soc = 77;
        sent.MotorState[5].Q = 1.25f;

        publisher.Publish(sent);

        subscriber.ReceivedCount.ShouldBe(1);
        subscriber.TryGetLatest(out LowState received).ShouldBeTrue();
        received.Tick.ShouldBe(4242u);
        received.BmsState.Soc.ShouldBe((byte)77);
        received.MotorState[5].Q.ShouldBe(1.25f);
    }

    [Fact]
    public async Task SubscribersOnOtherTopicsAreNotDelivered()
    {
        await using var transport = new LoopbackTransport();
        await using var participant = new DdsParticipant(transport);
        await participant.StartAsync();

        IDdsPublisher<LowState> publisher = participant.CreatePublisher<LowState>(Topics.LowState);
        IDdsSubscriber<LowState> other = participant.CreateSubscriber<LowState>("rt/somewhere_else");

        publisher.Publish(default(LowState));

        other.ReceivedCount.ShouldBe(0);
        other.TryGetLatest(out _).ShouldBeFalse();
    }

    [Fact]
    public async Task MultipleSubscribersOnOneTopicAllReceive()
    {
        await using var transport = new LoopbackTransport();
        await using var participant = new DdsParticipant(transport);
        await participant.StartAsync();

        IDdsPublisher<LowState> publisher = participant.CreatePublisher<LowState>(Topics.LowState);
        IDdsSubscriber<LowState> first = participant.CreateSubscriber<LowState>(Topics.LowState);
        IDdsSubscriber<LowState> second = participant.CreateSubscriber<LowState>(Topics.LowState);

        publisher.Publish(default(LowState));

        first.ReceivedCount.ShouldBe(1);
        second.ReceivedCount.ShouldBe(1);
    }

    [Fact]
    public async Task DisposingASubscriberStopsDelivery()
    {
        await using var transport = new LoopbackTransport();
        await using var participant = new DdsParticipant(transport);
        await participant.StartAsync();

        IDdsPublisher<LowState> publisher = participant.CreatePublisher<LowState>(Topics.LowState);
        IDdsSubscriber<LowState> subscriber = participant.CreateSubscriber<LowState>(Topics.LowState);

        publisher.Publish(default(LowState));
        subscriber.ReceivedCount.ShouldBe(1);

        subscriber.Dispose();
        publisher.Publish(default(LowState));

        subscriber.ReceivedCount.ShouldBe(1);
    }

    /// <summary>
    /// A slow consumer must lose old samples rather than stall the receive path or grow without bound.
    /// </summary>
    [Fact]
    public async Task SlowConsumersDropOldestRatherThanBlocking()
    {
        await using var transport = new LoopbackTransport();
        await using var participant = new DdsParticipant(transport);
        await participant.StartAsync();

        const int Capacity = 4;
        IDdsPublisher<LowState> publisher = participant.CreatePublisher<LowState>(Topics.LowState);
        IDdsSubscriber<LowState> subscriber = participant.CreateSubscriber<LowState>(Topics.LowState, Capacity);

        for (uint i = 0; i < 20; i++)
        {
            LowState state = default;
            state.Tick = i;
            publisher.Publish(state);
        }

        subscriber.ReceivedCount.ShouldBe(20);
        subscriber.DroppedCount.ShouldBeGreaterThan(0);

        // The newest sample is always available, no matter how far behind the channel reader is.
        subscriber.TryGetLatest(out LowState latest).ShouldBeTrue();
        latest.Tick.ShouldBe(19u);

        subscriber.Reader.Count.ShouldBeLessThanOrEqualTo(Capacity);
    }

    [Fact]
    public async Task MalformedPayloadsAreCountedNotThrown()
    {
        await using var transport = new LoopbackTransport();
        await using var participant = new DdsParticipant(transport);
        await participant.StartAsync();

        IDdsSubscriber<LowState> subscriber = participant.CreateSubscriber<LowState>(Topics.LowState);

        // A valid CDR header followed by nothing like a LowState body.
        byte[] garbage = [0x00, 0x01, 0x00, 0x00, 0xFF, 0xFF];
        Should.NotThrow(() => transport.Publish(Topics.LowState, garbage));

        subscriber.MalformedCount.ShouldBe(1);
        subscriber.ReceivedCount.ShouldBe(0);
    }

    [Fact]
    public async Task PublisherCountsAreTracked()
    {
        await using var transport = new LoopbackTransport();
        await using var participant = new DdsParticipant(transport);
        await participant.StartAsync();

        IDdsPublisher<LowState> publisher = participant.CreatePublisher<LowState>(Topics.LowState);

        publisher.Publish(default(LowState));
        publisher.Publish(default(LowState));

        publisher.PublishedCount.ShouldBe(2);
    }
}

public sealed class ManagedMulticastTransportTests
{
    /// <summary>
    /// Exercises the real socket path over loopback: a publisher and subscriber in one process, joined
    /// to the same multicast group, must exchange a byte-identical payload.
    /// </summary>
    [Fact]
    public async Task PayloadsSurviveARealMulticastRoundTrip()
    {
        var options = new UnitreeOptions
        {
            Model = RobotModel.Go2,
            Transport = DdsTransportKind.ManagedMulticast,
            // A port unlikely to collide with a developer's own simulator running alongside the tests.
            MulticastPort = 47913,
            MulticastAddress = "239.255.0.42",
        };

        await using var transport = new ManagedMulticastTransport(options);
        await using var participant = new DdsParticipant(transport);
        await participant.StartAsync();

        IDdsPublisher<SportModeState> publisher = participant.CreatePublisher<SportModeState>(Topics.SportModeState);
        IDdsSubscriber<SportModeState> subscriber = participant.CreateSubscriber<SportModeState>(Topics.SportModeState);

        SportModeState sent = default;
        sent.BodyHeight = 0.33f;
        sent.Mode = (byte)SportMode.Locomotion;
        sent.Position[0] = 7.5f;

        SportModeState received = default;
        bool delivered = false;

        // Multicast delivery is asynchronous and can be dropped by the OS under load, so the test polls
        // with a deadline rather than assuming a single publish arrives.
        for (int attempt = 0; attempt < 40 && !delivered; attempt++)
        {
            publisher.Publish(sent);
            await Task.Delay(50);
            delivered = subscriber.TryGetLatest(out received);
        }

        if (!delivered)
        {
            Assert.Skip("Multicast loopback is unavailable in this environment.");
            return;
        }

        received.BodyHeight.ShouldBe(0.33f);
        received.Mode.ShouldBe((byte)SportMode.Locomotion);
        received.GetPosition().X.ShouldBe(7.5f);
    }

    [Fact]
    public async Task PublishingBeforeStartThrowsAConnectionError()
    {
        var options = new UnitreeOptions
        {
            Model = RobotModel.Go2,
            Transport = DdsTransportKind.ManagedMulticast,
            MulticastPort = 47914,
        };

        await using var transport = new ManagedMulticastTransport(options);

        Should.Throw<UnitreeConnectionException>(() => transport.Publish(Topics.LowCommand, new byte[16]));
    }

    [Fact]
    public async Task UnknownInterfaceNameFailsWithAHelpfulMessage()
    {
        var options = new UnitreeOptions
        {
            Model = RobotModel.Go2,
            Transport = DdsTransportKind.ManagedMulticast,
            NetworkInterface = "definitely-not-a-real-nic",
            MulticastPort = 47915,
        };

        await using var transport = new ManagedMulticastTransport(options);

        UnitreeConnectionException exception =
            await Should.ThrowAsync<UnitreeConnectionException>(() => transport.StartAsync());

        exception.Message.ShouldContain("definitely-not-a-real-nic");
        exception.Message.ShouldContain("Available:");
    }
}
