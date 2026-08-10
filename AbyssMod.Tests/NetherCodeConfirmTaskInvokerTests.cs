#nullable enable

using System;
using AbyssMod.Services;
using Xunit;

namespace AbyssMod.Tests;

public class NetherCodeConfirmTaskInvokerTests
{
    [Fact]
    public void Invoke_calls_exact_full_confirmation_task_once_with_live_controller_state()
    {
        FakeUtility.Reset();
        var party = new FakePartyModel();
        var token = new FakeCancellationToken(37);
        var controller = new FakeController(party, token);

        bool invoked = NetherCodeConfirmTaskInvoker.TryInvoke(
            controller,
            typeof(FakeUtility),
            20016,
            BindingFor<FakeController>(),
            out object? task,
            out string error
        );

        Assert.True(invoked, error);
        Assert.Same(FakeUtility.ReturnedTask, task);
        Assert.Equal(1, FakeUtility.CallCount);
        Assert.Same(controller, FakeUtility.Controller);
        Assert.Equal(20016, FakeUtility.CodeId);
        Assert.Same(party, FakeUtility.Party);
        Assert.Equal(token, FakeUtility.Token);
    }

    [Fact]
    public void Invoke_fails_before_native_call_when_inherited_cancellation_token_is_missing()
    {
        MissingTokenUtility.Reset();
        var controller = new MissingTokenController(new FakePartyModel());

        bool invoked = NetherCodeConfirmTaskInvoker.TryInvoke(
            controller,
            typeof(MissingTokenUtility),
            10012,
            BindingFor<MissingTokenController>(nameof(MissingTokenUtility.Confirm)),
            out object? task,
            out string error
        );

        Assert.False(invoked);
        Assert.Null(task);
        Assert.Equal(0, MissingTokenUtility.CallCount);
        Assert.Contains("_cancellationToken", error, StringComparison.Ordinal);
    }

    private static NetherCodePopupInteropMethodBinding BindingFor<TController>(
        string methodName = nameof(FakeUtility.Confirm)
    ) => new(
        methodName,
        null,
        new[]
        {
            typeof(TController).FullName!,
            "System.Int64",
            typeof(FakePartyModel).FullName!,
            typeof(FakeCancellationToken).FullName!,
        },
        typeof(FakeUniTask).FullName!
    ) { IsStatic = true };

    private class FakeControllerBase
    {
        protected readonly FakeCancellationToken _cancellationToken;

        protected FakeControllerBase(FakeCancellationToken cancellationToken) =>
            _cancellationToken = cancellationToken;
    }

    private sealed class FakeController : FakeControllerBase
    {
        private readonly FakePartyModel _partyModel;

        public FakeController(FakePartyModel partyModel, FakeCancellationToken cancellationToken)
            : base(cancellationToken) => _partyModel = partyModel;
    }

    private sealed class MissingTokenController
    {
        private readonly FakePartyModel _partyModel;

        public MissingTokenController(FakePartyModel partyModel) => _partyModel = partyModel;
    }

    private sealed class FakePartyModel
    {
    }

    private readonly record struct FakeCancellationToken(int Value);

    private sealed class FakeUniTask
    {
    }

    private static class FakeUtility
    {
        public static readonly FakeUniTask ReturnedTask = new();
        public static int CallCount { get; private set; }
        public static FakeController? Controller { get; private set; }
        public static long CodeId { get; private set; }
        public static FakePartyModel? Party { get; private set; }
        public static FakeCancellationToken Token { get; private set; }

        public static FakeUniTask Confirm(
            FakeController controller,
            long codeId,
            FakePartyModel party,
            FakeCancellationToken token
        )
        {
            CallCount++;
            Controller = controller;
            CodeId = codeId;
            Party = party;
            Token = token;
            return ReturnedTask;
        }

        public static void Reset()
        {
            CallCount = 0;
            Controller = null;
            CodeId = 0;
            Party = null;
            Token = default;
        }
    }

    private static class MissingTokenUtility
    {
        public static int CallCount { get; private set; }

        public static FakeUniTask Confirm(
            MissingTokenController controller,
            long codeId,
            FakePartyModel party,
            FakeCancellationToken token
        )
        {
            CallCount++;
            return new FakeUniTask();
        }

        public static void Reset() => CallCount = 0;
    }
}
