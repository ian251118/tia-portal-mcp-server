# Third-party notices

## Not included: Siemens TIA Portal Openness

This project builds against `Siemens.Engineering.dll` and `Siemens.Engineering.Hmi.dll`, which belong to Siemens AG
and are part of TIA Portal. They are **not** distributed with this project: the build reads them from your own
TIA Portal installation, and at run time they are loaded from there. Using them is subject to your Siemens licence.

## Bundled with the build output

### Newtonsoft.Json 13.0.3

Copyright (c) 2007 James Newton-King. MIT licence. <https://www.newtonsoft.com/json>

### Sharp7 1.1.60

Copyright (c) 2018 Federico Barresi. MIT licence. <https://github.com/fbarresi/Sharp7>
Used for the read-only S7 client behind the `tia_s7_*` tools.

## Source adapted from other projects

### LAD SimaticML builder - `src/Lad/LadXmlBuilder.cs`

Adapted from the `LadXmlBuilder` of **hadefuwa/tia-portal-mcp** (<https://github.com/hadefuwa/tia-portal-mcp>),
which its README declares to be under the MIT licence. Copyright belongs to its authors. Changes made here:
namespace, JSON binding, error messages that name the failing network, and a check for static members on FCs.

## Build-time only

### Microsoft.NETFramework.ReferenceAssemblies 1.0.3

Copyright (c) Microsoft Corporation. MIT licence. Lets the .NET SDK compile for .NET Framework 4.8 without the
Developer Pack; nothing from it ends up in the output.

---

## MIT licence text (applies to the MIT-licensed components above)

Permission is hereby granted, free of charge, to any person obtaining a copy of this software and associated
documentation files (the "Software"), to deal in the Software without restriction, including without limitation the
rights to use, copy, modify, merge, publish, distribute, sublicense, and/or sell copies of the Software, and to
permit persons to whom the Software is furnished to do so, subject to the following conditions:

The above copyright notice and this permission notice shall be included in all copies or substantial portions of
the Software.

THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR IMPLIED, INCLUDING BUT NOT LIMITED TO THE
WARRANTIES OF MERCHANTABILITY, FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE AUTHORS OR
COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR
OTHERWISE, ARISING FROM, OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE SOFTWARE.
