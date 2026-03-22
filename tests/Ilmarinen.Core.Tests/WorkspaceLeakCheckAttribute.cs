using NUnit.Framework.Interfaces;
using NUnit.Framework;
using System.IO;
using System;

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
