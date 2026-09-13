#nullable enable
using System;
using System.Runtime.CompilerServices;

using FancyWM.Utilities;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace FancyWM.Tests.Utilities
{
    [TestClass]
    public class LowLevelHookPartialConstructionTest
    {
        [DataTestMethod]
        [DataRow(false)]
        [DataRow(true)]
        public void FinalizerRequestToleratesUninitializedOwner(bool mouse)
        {
            object owner = RuntimeHelpers.GetUninitializedObject(mouse
                ? typeof(LowLevelMouseHook)
                : typeof(LowLevelKeyboardHook));
            try
            {
                if (owner is LowLevelMouseHook mouseHook)
                {
                    mouseHook.RequestFinalizerStopForTest();
                }
                else
                {
                    ((LowLevelKeyboardHook)owner).RequestFinalizerStopForTest();
                }
            }
            finally
            {
                GC.SuppressFinalize(owner);
            }
        }
    }
}
