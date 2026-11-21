# BlazorApp: Razor markup, Typhon code-behind

A Blazor Web App (server-side rendering with interactive server components) in which every component keeps its
markup in a `.razor` file and its logic (state, event handlers, lifecycle methods, computed properties, injected
services) in a `.ty` file. The startup code (`main.ty`) and the services are Typhon too; there is no C# file.

```sh
dotnet run --project samples/BlazorApp                              # http://localhost:5080
dotnet run --project samples/BlazorApp -- --urls http://127.0.0.1:5000
```

It needs the project (the Razor compiler runs in the build), so `typhon run main.ty` cannot run it. The C# that Typhon
generates is in `obj/Debug/net11.0/generated/Typhon.Compiler/`, the Razor compiler's output next to it.

| Page | Markup | Code-behind | Shows |
|---|---|---|---|
| `/` | [Home.razor](Components/Pages/Home.razor) | [home.ty](Components/Pages/home.ty) | a static server-rendered page (no `@rendermode`), `OnInitialized`, a class variable listed with `@foreach` |
| `/counter`, `/counter/{start}` | [Counter.razor](Components/Pages/Counter.razor) | [counter.ty](Components/Pages/counter.ty) | fields, `@onclick` handlers (also with arguments), `@bind`, a route `[Parameter]`, `OnParametersSet`, computed properties |
| `/todos` | [Todos.razor](Components/Pages/Todos.razor) | [todos.ty](Components/Pages/todos.ty) | an `[Inject]`ed service, an `<EditForm>` validated by DataAnnotations attributes, `@bind:event="oninput"`, list comprehensions and `sorted` behind the markup |
| `/weather` | [Weather.razor](Components/Pages/Weather.razor) | [weather.ty](Components/Pages/weather.ty) | `async def OnInitializedAsync`, an async handler with `@bind:after`, `StateHasChanged`, `statistics`, a child component |
| (child) | [BarChart.razor](Components/Shared/BarChart.razor) | [bar_chart.ty](Components/Shared/bar_chart.ty) | component parameters, an SVG chart whose geometry is computed in Python |

The services live in [services/](services): `TodoStore` (a singleton, so the list survives reloads) and
`WeatherService` (an async API with a simulated delay). [main.ty](main.ty) registers them and maps the components.

## How the markup binds to the Typhon class

The Razor compiler turns `Counter.razor` into `public partial class Counter`. Typhon compiles `counter.ty` into
`public static partial class counter` (the module) in the namespace of its folder, `BlazorApp.Components.Pages`, with
the module's classes nested in it. Typhon's class cannot be the other half of Razor's partial class, so it is the
component's base class:

```python
# Components/Pages/counter.ty
class CounterBase(ComponentBase):
    start: Annotated[int, Parameter()] = 0
    count: int = 0

    def increment(self) -> None:
        self.count += 1
```

```razor
@* Components/Pages/Counter.razor: same namespace, so the module's name is enough *@
@page "/counter/{start:long}"
@rendermode InteractiveServer
@inherits counter.CounterBase

<p>Current count: @count</p>
<button @onclick="increment">Add one</button>
```

What the markup can use, and what it becomes in C#:

* **Fields.** Class-level annotations (`count: int = 0`) and attributes assigned in methods (`self.message = ...`) are
  public properties (`public long count { get; set; } = 0;`), so `@count`, `@bind="step"` and
  `@bind-Value="form.title"` work on them. A name starting with `_` is `protected internal`, which the markup still
  sees (the Razor class derives from the Typhon class, in the same assembly); a name starting with `__` is private.
  Python's `int` is a C# `long`: number inputs bind to it, and a route parameter takes the `long` constraint
  (`{start:long}`), since the router passes the value with the constraint's type (`int32` is Typhon's C# `int`).
* **Computed values.** A `@property` is a C# property: `@parity`, `@foreach (var todo in visible)`.
* **Event handlers.** `def increment(self) -> None` is `public virtual void increment()`, so `@onclick="increment"`
  binds the method; a handler with arguments is called from a C# lambda in the markup: `@onclick="() => remove(todo)"`.
  `async def reload(self) -> None` returns a `Task`, which Blazor awaits (`@bind:after="reload"`).
* **Lifecycle methods.** A method named like a virtual member of `ComponentBase` overrides it, with that member's
  access: `def OnParametersSet(self) -> None` becomes `protected override void OnParametersSet()`, and
  `async def OnInitializedAsync(self) -> None` becomes `protected override async Task OnInitializedAsync()`.
* **Parameters.** .NET attributes on a property come from `typing.Annotated`: `start: Annotated[int, Parameter()]`
  is `[Parameter()] public long start { get; set; }`. The router sets it from the URL; a parent sets a child's
  parameters with attributes on its tag.
* **Services.** `store: Annotated[TodoStore, Inject()]` is an `[Inject]` property, which Blazor fills from the app's
  services after constructing the component (`builder.Services.AddSingleton[TodoStore]()` in `main.ty`). Constructor
  injection is not available: Razor's half of the class has a parameterless constructor.
* **Validation.** DataAnnotations attributes go on a form model the same way:
  `title: Annotated[str, Required(ErrorMessage="..."), StringLength(40)] = ""`, checked by `<DataAnnotationsValidator />`.
* **Rendering.** `self.StateHasChanged()` renders in the middle of a handler (here: "Refreshing..." while the forecast
  loads); Blazor renders after each handler and lifecycle method anyway.

## What the Razor compiler cannot see

The Razor compiler and Typhon are both source generators of the same compilation, and neither sees what the other
generates; C# compiles both outputs together. So the build checks every name the markup uses (a typo in `@cout` is an
error on that line of the `.razor` file), but the Razor compiler itself knows nothing about the Typhon classes:

* `main.ty` cannot import the `App` component, so it names it in a string, like a forward reference in a Python
  annotation: `app.MapRazorComponents["Components.App"]()`.
* A component whose base class is a Typhon class is not recognized as a component when another component uses its tag.
  [BarChart.razor](Components/Shared/BarChart.razor) declares `@implements IComponent` for that
  (`ComponentBase` implements it already, so nothing else changes). Its parameters are still unknown to the Razor
  compiler, so their values are passed untyped and matched by name (case-insensitively) when the page renders: pass
  every value with `@` (`values="@temperatures"`; `values="temperatures"` would be the string), make the types match
  exactly (a `list[float]` property for a `list[float]` parameter), and create an `EventCallback` parameter's value
  yourself (`on_pick="@EventCallback.Factory.Create(this, pick)"`). Routable pages are found at run time and have none
  of these limits.
* The Razor editor offers no completion for the Typhon members.

For typed child-component parameters, put the Typhon classes in a class library that the Razor project references:
the Razor compiler then reads them like any other referenced assembly.
