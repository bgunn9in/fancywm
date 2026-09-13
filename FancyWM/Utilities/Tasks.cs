using System.Collections.Generic;
using System.Threading.Tasks;

namespace FancyWM.Utilities
{
    internal static class Tasks
    {
        public static async Task WhenAllIgnoreCancelled(IEnumerable<Task> enumerable)
        {
            var completion = Task.WhenAll(enumerable);
            try
            {
                await completion;
            }
            catch (TaskCanceledException) when (completion.IsCanceled)
            {
            }
        }
    }
}
