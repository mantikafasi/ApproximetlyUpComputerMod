# Third-Party Notices

The repository's MIT license covers the mod's original code, documentation and
authored AU-08 model assets. It does not relicense dependencies or the game.
Keep this file with distributions containing `MoonSharp.Interpreter.dll`.

## MoonSharp 2.0.0

Restored from NuGet, not vendored in this repository. The following BSD-style
three-clause license is reproduced from the upstream
[v2.0.0.0 LICENSE](https://github.com/moonsharp-devs/moonsharp/blob/v2.0.0.0/LICENSE).
The upstream placeholder `{organization}` is retained verbatim. Debugger/icon
credits belong to the upstream project; those tools/icons are not bundled here.

```text
Copyright (c) 2014-2016, Marco Mastropaolo
All rights reserved.

Parts of the string library are based on the KopiLua project (https://github.com/NLua/KopiLua)
Copyright (c) 2012 LoDC

Visual Studio Code debugger code is based on code from Microsoft vscode-mono-debug project (https://github.com/Microsoft/vscode-mono-debug).
Copyright (c) Microsoft Corporation - released under MIT license.

Remote Debugger icons are from the Eclipse project (https://www.eclipse.org/).
Copyright of The Eclipse Foundation

The MoonSharp icon is (c) Isaac, 2014-2015

Redistribution and use in source and binary forms, with or without
modification, are permitted provided that the following conditions are met:

* Redistributions of source code must retain the above copyright notice, this
  list of conditions and the following disclaimer.

* Redistributions in binary form must reproduce the above copyright notice,
  this list of conditions and the following disclaimer in the documentation
  and/or other materials provided with the distribution.

* Neither the name of the {organization} nor the names of its
  contributors may be used to endorse or promote products derived from
  this software without specific prior written permission.

THIS SOFTWARE IS PROVIDED BY THE COPYRIGHT HOLDERS AND CONTRIBUTORS "AS IS"
AND ANY EXPRESS OR IMPLIED WARRANTIES, INCLUDING, BUT NOT LIMITED TO, THE
IMPLIED WARRANTIES OF MERCHANTABILITY AND FITNESS FOR A PARTICULAR PURPOSE ARE
DISCLAIMED. IN NO EVENT SHALL THE COPYRIGHT HOLDER OR CONTRIBUTORS BE LIABLE
FOR ANY DIRECT, INDIRECT, INCIDENTAL, SPECIAL, EXEMPLARY, OR CONSEQUENTIAL
DAMAGES (INCLUDING, BUT NOT LIMITED TO, PROCUREMENT OF SUBSTITUTE GOODS OR
SERVICES; LOSS OF USE, DATA, OR PROFITS; OR BUSINESS INTERRUPTION) HOWEVER
CAUSED AND ON ANY THEORY OF LIABILITY, WHETHER IN CONTRACT, STRICT LIABILITY,
OR TORT (INCLUDING NEGLIGENCE OR OTHERWISE) ARISING IN ANY WAY OUT OF THE USE
OF THIS SOFTWARE, EVEN IF ADVISED OF THE POSSIBILITY OF SUCH DAMAGE.
```

## External Runtime References

BepInEx, Harmony, Il2CppInterop and Unity/game assemblies are referenced from the
developer's local game installation. They are not redistributed in this source
repository or copied by the plugin build. Install them from their respective
providers and follow their licenses. Approximately Up and its extracted assets,
metadata, generated interop assemblies and player saves are not covered by the
mod's license and must not be added to the repository.
