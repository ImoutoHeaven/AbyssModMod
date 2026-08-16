using AbyssMod.Services;
using Xunit;

namespace AbyssMod.Tests;

public class TavernFirstCardHookPlanTests
{
    [Fact]
    public void Fresh_native_hook_uses_instance_pre_model_seam_instead_of_static_generic_api()
    {
        Assert.Equal(
            "Project.Tavern.Top.GameViewController",
            TavernFirstCardHookPlan.InterceptionType
        );
        Assert.Equal("CreateGameData", TavernFirstCardHookPlan.InterceptionMethod);
        Assert.False(TavernFirstCardHookPlan.InterceptsStaticGenericUniTask);
        Assert.True(TavernFirstCardHookPlan.IsNativeAbiSafe);
    }
}
