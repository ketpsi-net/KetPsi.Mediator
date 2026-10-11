using Xunit;

namespace KetPsi.Mediator.Tests;

public class UnitTests
{
    [Fact]
    public void Unit_Value_IsDefault()
    {
        Assert.Equal(default, Unit.Value);
    }

    [Fact]
    public void Unit_Equals_AlwaysTrue()
    {
        Assert.True(Unit.Value.Equals(Unit.Value));
        Assert.True(Unit.Value.Equals(default(Unit)));
        Assert.True(Unit.Value == default(Unit));
        Assert.False(Unit.Value != default(Unit));
    }

    [Fact]
    public void Unit_EqualsObject_OnlyUnit()
    {
        Assert.True(Unit.Value.Equals((object)Unit.Value));
        Assert.False(Unit.Value.Equals(null));
        Assert.False(Unit.Value.Equals("()"));
    }

    [Fact]
    public void Unit_GetHashCode_IsStable()
    {
        Assert.Equal(0, Unit.Value.GetHashCode());
        Assert.Equal(Unit.Value.GetHashCode(), default(Unit).GetHashCode());
    }

    [Fact]
    public void Unit_ToString_IsEmptyParens()
    {
        Assert.Equal("()", Unit.Value.ToString());
    }
}
