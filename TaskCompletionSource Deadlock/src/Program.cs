using System;
using System.Threading;
using System.Threading.Tasks;

namespace ConsoleApp1
{
    class Program
    {
        private static ManualResetEventSlim _mutex = new ManualResetEventSlim();
        private static Task _sharedTask;

        static void Main(string[] args)
        {
            var tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            _sharedTask = tcs.Task;

            var t1 = Task.Run(Thread1);
            Thread.Sleep(1000); // Wait for the task to start

            var t2 = Task.Run(Thread2);
            Thread.Sleep(1000); // Wait for the task to start

            Console.WriteLine($"Completing the shared task in thread #{Thread.CurrentThread.ManagedThreadId}");
            tcs.SetResult(true);

            Console.WriteLine("Done Main");
        }

        private static async Task Thread1()
        {
            Console.WriteLine($"T1 started in thread #{Thread.CurrentThread.ManagedThreadId}");
            await _sharedTask;
            Console.WriteLine($"T1 continued in thread #{Thread.CurrentThread.ManagedThreadId}");
            _mutex.Wait();

            Console.WriteLine("Done T1");
        }

        private static async Task Thread2()
        {
            Console.WriteLine($"T2 started in thread #{Thread.CurrentThread.ManagedThreadId}");
            await _sharedTask;
            Console.WriteLine($"T2 continued in thread #{Thread.CurrentThread.ManagedThreadId}");
            _mutex.Set();

            Console.WriteLine("Done T2");
        }
    }
}