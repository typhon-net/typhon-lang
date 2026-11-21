# Desktop apps in Typhon: Windows Forms, WPF and WinUI 3

The same small to-do app three times, once per .NET desktop framework: a text box and an Add button (also Enter), a
list of check boxes, a status line ("1 of 3 done"), "Remove done", and "Load examples", which fetches items on a worker
thread while the window stays responsive. Each app is an ordinary .NET project whose sources are `.ty` files, like
[`HelloTyphon`](../HelloTyphon).

| Folder | Framework | The window | Project | In `Typhon.slnx` |
|---|---|---|---|---|
| [`WinForms`](WinForms) | Windows Forms | built in code (`todo_form.ty`), what the designer would generate | `WinFormsTodo.csproj` | yes |
| [`Wpf`](Wpf) | WPF | XAML (`TodoView.xaml`) compiled to BAML, loaded and wired in `todo_window.ty` | `WpfTodo.csproj` | yes |
| [`WinUI`](WinUI) | WinUI 3, Windows App SDK 2.7, unpackaged | XAML (`TodoView.xaml`) parsed at run time, wired in `todo_window.ty` | `WinUITodo.csproj` | no |
| [`Shared`](Shared) | none | `todos.ty`: the model, plain Typhon that each project compiles with its own files | | |

Each app has an entry module `app.ty` (what `Program.cs` or `App.xaml` is in C#) and a window module.

## Prerequisites

* The .NET 11 SDK. Windows Forms and WPF projects build on Windows, Linux and macOS (`EnableWindowsTargeting`) but run
  only on Windows. On a Windows machine without the .NET 11 runtime, publish self-contained (below).
* WinUI 3 also needs the Windows App Runtime 2.x (2.7 or later) installed on the machine that runs it, from the
  [Windows App SDK downloads](https://learn.microsoft.com/windows/apps/windows-app-sdk/downloads): the app is
  framework-dependent and finds the runtime through the Windows App SDK's bootstrapper. The NuGet packages (a few hundred
  MB) come with the first restore. Windows 10 1809 or later.

## Build and run

```powershell
dotnet run --project samples/Desktop/WinForms
dotnet run --project samples/Desktop/Wpf
dotnet run --project samples/Desktop/WinUI

# --smoke: a scripted run that types, clicks and ticks through the window, prints what it shows and exits (0 = passed).
# A Windows (GUI) program's output reaches the console only when it is redirected, hence the pipe.
dotnet run --project samples/Desktop/Wpf -- --smoke | Out-String

# on a Windows machine without the .NET 11 runtime: a self-contained build
dotnet publish samples/Desktop/Wpf -c Release -r win-x64 --self-contained -o out/wpf
out/wpf/WpfTodo.exe --smoke | Out-String
```

`dotnet build` works on Linux and macOS for all three (WinUI runs no Windows tool in this configuration); run the result
on Windows. Under WSL, publish to a Windows folder (`-o /mnt/c/...`) and start the `.exe` from the WSL shell: its output
comes back to the terminal. The smoke run shows the real window and runs the framework's message loop, but places the window off-screen
without a taskbar button, and ends the process after 60 seconds if the script got stuck. All three print the same,
apart from the first line:

```
WinUI 3 on .NET 11.0.0, UI thread: ApartmentState.STA
added two: ['[ ] Buy milk', '[ ] Write a Typhon sample'] - 0 of 2 done
Add enabled for a blank title: False
ticked the first: ['[x] Buy milk', '[ ] Write a Typhon sample'] - 1 of 2 done
removed done: ['[ ] Write a Typhon sample'] - 0 of 1 done
loading: Loading… - Load enabled: False
loaded examples: ['[ ] Write a Typhon sample', '[ ] Read docs/language.md', '[ ] Try typhon run', '[ ] Port a script'] - 0 of 4 done
back on the UI thread after await: True
```

The WinUI project is not in `Typhon.slnx` because of the size of the Windows App SDK packages each CI run would
restore. `-p:WindowsAppSDKSelfContained=true` ships the Windows App Runtime with the app instead of requiring it; that
build runs Windows tools (`mt.exe`, `makepri.exe`), so it works only on Windows, and it is not tested here.

## How the Typhon code maps to the frameworks

**Classes.** `class TodoForm(Form):` and `class TodoWindow(Window):` are C# subclasses of the framework classes
(`public partial class TodoForm : Form`, inside the module's static class `todo_form`). `super().__init__()` is the
base constructor call; `self.Text = "..."` sets the inherited .NET property; `self.todos = TodoList()` declares a
property of the subclass. Python ints go into `int` properties and parameters with an overflow check
(`Size(420, 360)`, `SetColumnSpan(list, 2)`), .NET ints come back as Python ints, `AnchorStyles.Left |
AnchorStyles.Right` combines flags. A method named like a virtual .NET method overrides it, with the access C# requires:
`def OnLaunched(self, args: LaunchActivatedEventArgs)` in the WinUI `App` becomes `protected override void
OnLaunched(...)`. A parameter that is a .NET `int` in the base method needs an `int32` annotation.

**Events.** `button.Click += self.on_add` subscribes a bound method; its parameters are the delegate's, `(self, sender:
object, e: EventArgs) -> None`. A lambda works too: `self.Loaded += lambda sender, e: self.input.Focus()`. A delegate
can be made explicitly, as WPF's `AddHandler` needs: `self.list.AddHandler(ToggleButton.CheckedEvent,
RoutedEventHandler(self.on_toggled))` handles the Checked event that bubbles up from every item's CheckBox. Handlers run
on the UI thread.

**Threads.** Windows Forms, WPF and WinUI need the UI thread to be a single-threaded COM apartment, which the CLR sets up
from `[STAThread]` on the entry point. Typhon generates `Main` itself, so the attribute goes on the function that starts
the UI, and Typhon puts it on `Main` too:

```python
@STAThread
def main(args: list[str]) -> None:
    Application.Run(TodoForm())


if __name__ == "__main__":
    main(sys.argv[1:])
```

Long work belongs on another thread. `async def load_examples(self)` is a .NET `Task` method: `titles = await
Task.Run(example_titles)` runs the slow function on the thread pool, and the code after `await` continues on the UI
thread, because each framework installs a `SynchronizationContext` there (WinUI's comes from `start()` in `app.ty`,
as in the `Main` that WinUI's XAML compiler generates for C#). A .NET event handler returns nothing, so it cannot be an
`async def`; the handler calls the coroutine, which starts it (a Typhon coroutine runs as soon as it is called), and
keeps the Task: `self.loading = self.load_examples()`. Do not call `asyncio.run` on the UI thread: it runs an event
loop of its own and blocks the window until it returns. To touch controls from a thread you started yourself, post a
lambda to the UI thread: `form.BeginInvoke(...)`, `window.Dispatcher.InvokeAsync(...)`,
`window.DispatcherQueue.TryEnqueue(...)`.

**XAML.** Windows Forms has none: the designer writes C#, and Typhon code builds the same controls. For WPF and WinUI the
XAML has no `x:Class`. With one, the XAML compiler generates a C# partial class (`InitializeComponent`, a field per
`x:Name`) that has to pair with the class you write; Typhon's classes are C# generated by a source generator, which the
XAML build steps do not see, and they are nested in their module's class. Instead the Typhon class loads the XAML and
looks up the named elements with `FindName`, typed with `typing.cast`:

```python
view = cast(FrameworkElement, Application.LoadComponent(Uri("/WpfTodo;component/TodoView.xaml", UriKind.Relative)))
self.Content = view
self.add_button = cast(Button, view.FindName("AddButton"))
self.add_button.Click += self.on_add
```

* **WPF** compiles `TodoView.xaml` to BAML during the build (on any OS), so markup errors are build errors, and
  `Application.LoadComponent` loads it. The item template binds to the model's objects (`{Binding title}`): fields of a
  Typhon class are .NET properties, which data binding reads by reflection.
* **WinUI 3**'s XAML compiler runs only on Windows and generates C# for the app's types, so the project embeds
  `TodoView.xaml` as a resource instead of compiling it, and `XamlReader.Load` parses it at run time. It may use only
  types XAML knows without generated code (the framework's), and no `x:Class` or event handlers in the markup. The XAML
  compiler would also make the `App` class tell XAML about WinUI's control library (`IXamlMetadataProvider`), which
  `XamlControlsResources`, the Fluent styles, needs. That interface has two `GetXamlType` overloads, which a Typhon
  class cannot declare, so the sample's only C# file, [`XamlApplication.cs`](WinUI/XamlApplication.cs), implements it
  in the base class of the Typhon `App`. The list's items are CheckBoxes made in code, because WinUI's Checked events do
  not bubble to the list as WPF's do; `{Binding}` to Typhon objects works in WinUI as well.
* The unpackaged WinUI app needs the manifest of the WinUI template (`app.manifest`, Windows 10 compatibility and
  per-monitor DPI), and `Application.Start` instead of a generated `Main`: the callback installs the UI thread's
  `SynchronizationContext` and creates the `App`, whose `OnLaunched` opens the window.

**Gaps you may meet.** `isinstance()` narrows only to Typhon classes, so a .NET type needs a `cast()` in the branch
(`find_check_box` in the WPF window). A module cannot define a function with its own name (`def main()` in `main.ty`
clashes with the module's class), hence `app.ty`. Code generated per compilation by other source generators (XAML,
C#/WinRT's AOT support, `[LibraryImport]`) does not see Typhon's classes; under Native AOT or trimming, WinUI data
binding and the C#/WinRT interop of Typhon classes are untested.
