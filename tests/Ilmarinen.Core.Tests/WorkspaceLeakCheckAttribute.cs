using NUnit.Framework;
using NUnit.Framework.Interfaces;

[assembly: WorkspaceLeakCheck]

[AttributeUsage(AttributeTargets.Assembly)]
public class WorkspaceLeakCheckAttribute : Attribute, ITestAction
{
    public ActionTargets Targets => ActionTargets.Test;

    public void BeforeTest(ITest test) { }

    public void AfterTest(ITest test)
    {
        if (Directory.Exists("/workspace"))
        {
            Assert.Fail("Leaked /workspace directory - container bind mount not cleaned up");
        }
    }
}
