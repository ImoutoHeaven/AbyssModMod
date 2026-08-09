#nullable enable

using System;
using AbyssMod.Services;
using Xunit;

namespace AbyssMod.Tests
{
    public class NetherRuntimeIl2CppEnumerableTests
    {
        [Fact]
        public void Active_code_extractor_reads_il2cpp_generic_enumerator_via_non_generic_move_next()
        {
            var possessions = new FakeIl2CppEnumerable<FakePossessionCode>(
                new FakePossessionCode(30024, 2)
            );

            NetherActiveCodeErosionProjection projection =
                new NetherRuntimeActiveCodeErosionExtractor().Extract(
                    possessions,
                    new[] { new FakeMasterCode(30024, 6, 4, 0, 0) }
                );

            Assert.True(projection.ErosionProjectionKnown, projection.Detail);
            Assert.Equal(new long[] { 30024 }, projection.SortedCodeIds);
            NetherCodeEffect effect = Assert.Single(projection.ErosionEffects);
            Assert.Equal(NetherCodeEffectKind.ErosionAdditionUp, effect.EffectKind);
            Assert.Equal(4, effect.Amount);
        }

        [Fact]
        public void Unsupported_enumerator_shape_is_failure_and_never_an_empty_success()
        {
            bool success = NetherRuntimeEnumerableReader.TryRead(
                new UnsupportedEnumerable(),
                out var values,
                out string detail
            );

            Assert.False(success);
            Assert.Empty(values);
            Assert.Equal("move-next-unavailable", detail);
        }
    }

    internal sealed class FakeIl2CppEnumerable<T>
    {
        private readonly T[] _values;

        public FakeIl2CppEnumerable(params T[] values) => _values = values;

        public FakeIl2CppGenericEnumerator<T> GetEnumerator() => new(_values);
    }

    internal sealed class UnsupportedEnumerable
    {
        public UnsupportedEnumerator GetEnumerator() => new();
    }

    internal sealed class UnsupportedEnumerator
    {
        public object Current => new();
    }

    internal sealed class FakeIl2CppGenericEnumerator<T>
    {
        private readonly Il2CppEnumeratorState<T> _state;

        public FakeIl2CppGenericEnumerator(T[] values) => _state = new(values);

        public T Current => _state.Current;

        public TCast? TryCast<TCast>() where TCast : class =>
            typeof(TCast).FullName == "Il2CppSystem.Collections.IEnumerator"
                ? (TCast)(object)new Il2CppSystem.Collections.IEnumerator(_state)
                : null;
    }

    internal sealed class Il2CppEnumeratorState<T>
    {
        private readonly T[] _values;
        private int _index = -1;

        public Il2CppEnumeratorState(T[] values) => _values = values;

        public T Current => _index >= 0 && _index < _values.Length
            ? _values[_index]
            : throw new InvalidOperationException("Enumerator is not positioned on an item.");

        public bool MoveNext()
        {
            if (_index >= _values.Length)
                return false;
            _index++;
            return _index < _values.Length;
        }
    }

    internal sealed class FakePossessionCode
    {
        public FakePossessionCode(long codeId, int amount)
        {
            MNetherCodeId = codeId;
            Amount = amount;
        }

        public long MNetherCodeId { get; }
        public int Amount { get; }
    }

    internal sealed class FakeMasterCode
    {
        public FakeMasterCode(long id, int effectType, long parameter1, long parameter2, long parameter3)
        {
            this.id = id;
            effect_type = effectType;
            effect_parameter_1 = parameter1;
            effect_parameter_2 = parameter2;
            effect_parameter_3 = parameter3;
        }

        public long id;
        public int effect_type;
        public long effect_parameter_1;
        public long effect_parameter_2;
        public long effect_parameter_3;
    }
}

namespace Il2CppSystem.Collections
{
    internal sealed class IEnumerator
    {
        private readonly Func<bool> _moveNext;

        public IEnumerator(object state)
        {
            _moveNext = (Func<bool>)state.GetType().GetMethod("MoveNext")!.CreateDelegate(
                typeof(Func<bool>),
                state
            );
        }

        public bool MoveNext() => _moveNext();
    }
}
