# Legacy SDR# reference assemblies (compile only)

SDR# can't be redistributed, so CI builds the .NET Framework version of the plugin (for older 32-bit SDR#, e.g.
1716) against these stand-ins for `SDRSharp.Common.dll` and `SDRSharp.Radio.dll`. They declare only the SDR# API the
plugin uses, with the same names, signatures and enum values as SDR# 1716. Like SDR#'s own assemblies they're not
strong-named and have version 0.0.0.0, so the plugin binds to the real SDR# assemblies at run time.

They are never shipped. To build against a real SDR# instead, pass its folder as `SdrSharpDir`.
