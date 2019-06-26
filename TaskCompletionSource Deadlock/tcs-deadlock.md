# Пара особенностей и бага *TPL*

<div class='epigraph'>

*"Let's also conclude a fact that is evident by now: Professional, highly experienced developers are practically unable to correctly use the TPL. It is humanly impossible."* -- Andrew Arnott, Microsoft Visual Studio Platform team.

</div>

Антон буквально сегодня подкинул для размышления короткую задачку, на полный разбор которой ушло многовато времени, но для понимания того, как взаимосвязаны задачи и потоки (*Task*, *Thread* соответственно) в .NET это оказалось весьма полезно.

Итак, собственно задача - что вообще произойдет при запуске такого консольного приложения, в каком порядке и что будет выведено?

```csharp
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
            var tcs = new TaskCompletionSource<bool>();
            _sharedTask = tcs.Task;

            var t1 = Task.Run(Thread1);
            Thread.Sleep(1000); // Wait for the task to start

            var t2 = Task.Run(Thread2);
            Thread.Sleep(1000); // Wait for the task to start

            Console.WriteLine("Completing the shared task");
            tcs.SetResult(true);

            Console.WriteLine("Done Main");
        }

        private static async Task Thread1()
        {
            await _sharedTask;
            _mutex.Wait();

            Console.WriteLine("Done T1");
        }

        private static async Task Thread2()
        {
            await _sharedTask;
            _mutex.Set();

            Console.WriteLine("Done T2");
        }
    }
}
```

Я решил, что здесь точно будут выведены в консоль сообщения из функции `main`, остальное непредсказуемо: может выведутся сообщения фоновых задач, может нет, порядок тоже может быть разным. 

А в реальности произойдет здесь deadlock, программа просто выведет

```
Completing the shared task
```

и зависнет.

Я не смог сходу объяснить, почему работает именно так, и попробовал разобраться. Для начала добавил логирования: 

```csharp
        Console.WriteLine($"Completing the shared task in thread #{Thread.CurrentThread.ManagedThreadId}");
        tcs.SetResult(true);

        Console.WriteLine("Done Main");
    }

    private static async Task Thread1()
    {
        Console.WriteLine($"T1 started in thread #{Thread.CurrentThread.ManagedThreadId}");
        await _sharedTask;
        _mutex.Wait();

        Console.WriteLine("Done T1");
    }

    private static async Task Thread2()
    {
        Console.WriteLine($"T2 started in thread #{Thread.CurrentThread.ManagedThreadId}");
        await _sharedTask;
        _mutex.Set();

        Console.WriteLine("Done T2");
    }
```

Получим:

```
T1 started in thread #4
T2 started in thread #4
Completing the shared task in thread #1
```

Ну вроде ожидаемо, `Task.Run()` из основного потока запустил задачи на `ThreadPool`, deadlock на месте. Здесь я несколько углубился в чтение статей и разборов на *StackOverflow* по данной теме и наткнулся на первую настоятельную рекомендацию (а может и правило?): 

  > Никаких явных блокирующих вызовов в _async_ функциях

вроде нашего `_mutex.Wait()`. Вторым открытием было то, что вызов `tcs.SetResult(true)` делает больше, чем кажется на первый взгляд. А именно, не только завершает задачу, но и сразу же вызывает _continuation_ для нее, причем **синхронно на том же потоке**. Добавим еще логов и закоментируем блокирующий вызов:

```csharp
    private static async Task Thread1()
    {
        Console.WriteLine($"T1 started in thread #{Thread.CurrentThread.ManagedThreadId}");
        await _sharedTask;
        Console.WriteLine($"T1 continued in thread #{Thread.CurrentThread.ManagedThreadId}");
        //_mutex.Wait();

        Console.WriteLine("Done T1");
    }

    private static async Task Thread2()
    {
        Console.WriteLine($"T2 started in thread #{Thread.CurrentThread.ManagedThreadId}");
        await _sharedTask;
        Console.WriteLine($"T1 continued in thread #{Thread.CurrentThread.ManagedThreadId}");
        //_mutex.Set();

        Console.WriteLine("Done T2");
    }
```

Вывод:

```
T1 started in thread #4
T2 started in thread #4
Completing the shared task in thread #1
T1 continued in thread #1
Done T1
T2 continued in thread #1
Done T2
Done Main
```

Надеюсь так стало понятнее, что именно происходит:
  * выполняем запуск фоновых задач `Thread1, Thread2` на `ThreadPool` 
  * они ожидают завершения третьей задачи `_sharedTask`
  * вызов завершения `_sharedTask` не только завершает саму задачу но и тут же на этом же потоке продолжает синхронно выполнение фоновых
  * при наличии блокирующего вызова - deadlock, так как всё в одном потоке выполнения

Неожиданно в этом то, что синхронно будет выполнен не только первый *continuation* (в нашем случае `Thread1`), но и вообще **все последующие** будут выполнены синхронно и **последовательно** один за другим. Это уже проблема реализации, что и обсуждалось [здесь](https://github.com/dotnet/corefx/issues/34781) *(эмоциональное высказывание из этой дискуссии стало эпиграфом к данной статье)* и, вероятно, будет исправлено в .NET Core 3, буквально неделю назад исправление было слито. Именно из-за этого вторая фоновая задача не могла разблокировать первую, а вместе с ней и всю программу.

Однако даже в исходном виде программу можно заставить работать, причем именно так, как я ожидал изначально *(настоятельно рекомендую ознакомиться вот с [этим комментарием](https://stackoverflow.com/a/39307345) Stephen Cleary)*. Для этого надо передать параметр в конструктор: `new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);`, вызов `tcs.SetResult(true)` перестает быть блокирующим.

Итоговый код:

```csharp
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
```

Можно запустить несколько раз и проверить что получится. Так же еще пара ссылок на статьи по теме: [The danger of TaskCompletionSource class](https://devblogs.microsoft.com/premier-developer/the-danger-of-taskcompletionsourcet-class/) и [New Task APIs in .NET 4.6](https://devblogs.microsoft.com/pfxteam/new-task-apis-in-net-4-6/) из Microsoft DevBlog.

Выводы, которые я сделал для себя:
  * Не блокировать *async* функции
  * Не полагаться на предположения об исполнении асинхронного кода на одном или разных потоках при реализации логики
  * В *TPL* всё сложнее/больше, чем представляется из описания классов/функции
  * Еще видел такую рекомендацию: если не уверены на 100%, на асинхронный код стоит смотреть и с точки зрения того, как он будет выполняться синхронно

Надеюсь кому-то было полезно, как мне. Если заметили ошибки - пишите, я дополню/поправлю.
