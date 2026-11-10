# Vector Field

<img width="1920" height="1080" alt="vectorfield-epsilon" src="https://github.com/user-attachments/assets/22aa120b-d004-4308-8983-a986c4762a0d" />

If you like it, a ⭐ on [Epsilon](https://github.com/XBambinoX/Epsilon) helps a lot!

An interactive viewer for plane vector fields **F(x, y, t) = [P, Q]**. Type any formula in the
window and watch thousands of particles flow along it.

All of the mathematics is done by [Epsilon](https://www.nuget.org/packages/Epsilon), a symbolic
math library for .NET. Drawing is plain Vulkan through [Silk.NET](https://github.com/dotnet/Silk.NET):
one tiny shader, colored triangles, 4x MSAA. There is no other library: the font, the text box and
the PNG writer are in this repository.

## What Epsilon does here

| Feature | Epsilon API |
|---|---|
| Parse the typed field, with readable errors (`Unknown identifier 'sen'`) | `Vector<Expr>.Parse(text, ["x", "y", "t"])` |
| Simplify it exactly (`0.2y` becomes `(1/5) * y`) | `Vector<Expr>.Simplify` |
| Divergence and curl as formulas | `Divergence`, `Curl` (Epsilon.Calculus) |
| A potential φ with ∇φ = F when the curl is zero | `Potential` (Epsilon.Calculus) |
| The Jacobian J and det(J − λI) as formulas | `Vector<Expr>.Jacobian`, `Matrix<Expr>.CharacteristicPolynomial("λ")` |
| Critical points: Newton's method on F = 0 from a grid of seeds | `Expr.Compile([P, Q, J11, J12, J21, J22], ...)`, `Matrix2x2<double>.TryInverse` |
| Their type from the trace and determinant of J | `Matrix2x2<double>.Trace`, `Determinant` |
| Evaluate F, div F and curl F millions of times per second | `Expr.Compile([P, Q, div, curl], ["x", "y", "t"])`: one delegate, shared subexpressions computed once |
| Points and vectors for particles, streamlines and the probe | `Vector2<double>`, `Norm`, `Distance` |
| Placing each arrow | `Matrix3x3<double>.Compose(translation, angle, scale)` |
| The camera | `Matrix4x4<float>.OrthographicOffCenter(..., DepthRange.ZeroToOne)` |

## Running

Requires the .NET 10 SDK, a Vulkan driver and `glslc` (from `shaderc`) to compile the shaders.

```bash
dotnet run -c Release
dotnet run -c Release -- --preset 7 --heatmap curl
dotnet run -c Release -- --formula "[sin(y + t), cos(x - t)]"
```

`dotnet run -c Release -- --help` lists every option, including `--capture shot.png` to render one
frame to a file and exit.

## Controls

| Key | |
|---|---|
| Enter, or click the formula | edit the field; Enter applies, Esc cancels, Ctrl+V pastes |
| 1 to 9 | presets |
| Space | pause |
| + / − | particle speed |
| A, P, S | arrows, particles, streamline under the cursor |
| C | critical points; hover one for its eigenvalues and Jacobian |
| H | heatmap: off, \|F\|, div F, curl F |
| drag, wheel | pan, zoom around the cursor |
| click, right click | pin a streamline, clear the pins |
| R | reset the view |
| Tab | hide the interface, for recording |
| F12 | save a screenshot to ~/Pictures |

The formula can use `x`, `y`, the time `t`, `pi`, `e` and every function Epsilon parses: `sin`,
`cos`, `exp`, `ln`, `sqrt`, `abs`, `atan`, `sinh`, `min`, `max`, `log(x, b)`, `nthroot(x, n)` and more.
Brackets are optional: `-y, x` works.

## License

MIT, copyright (c) 2026 Max Zakharov. See [LICENSE.txt](LICENSE.txt).
