Testing with CoreFX
===================

You can use CoreFX tests to validate your changes to CoreCLR.
The coreclr repo Azure DevOps CI system runs CoreFX tests against
every pull request, and regularly runs the CoreFX tests under
many different configurations (e.g., platforms, stress modes).

There are two basic options:

1. Build the CoreFX product and tests with a build of CoreCLR, or
2. Use a published snapshot of the CoreFX test build with a build of CoreCLR.

Mechanism #1 is the easiest. Mechanism #2 is how the CI system runs tests,
as it is optimized for our distributed Helix test running system.

# Building CoreFX against CoreCLR

In all cases, first build the version of CoreCLR that you wish to test. You do not need to build the coreclr tests. Typically, this will be a Checked build (faster than Debug, but with asserts that Release builds do not have). In the examples here, coreclr is assumed to be cloned to `f:\git\runtime`.

For example:
```
f:\git\runtime> src\coreclr\build.cmd x64 checked skiptests
```

Normally when you build CoreFX it is built against a "last known good" Release version of CoreCLR.

There are two options here:
1. Build CoreFX against your just-built CoreCLR.
2. Build CoreFX normally, against the "last known good" version of CoreCLR, and then overwrite the "last known good" CoreCLR.

Option #1 might fail to build if CoreCLR has breaking changes that have not propagated to CoreFX yet.
Option #2 should always succeed the build, since the CoreFX CI has verified this build already.
Option #2 is generally recommended.

## Building CoreFX against just-built CoreCLR

```
f:\git\runtime> src\libraries\build.cmd -configuration Release -arch x64 -restore -build -buildtests -test /p:CoreCLROverridePath=f:\git\runtime\artifacts\bin\coreclr\Windows_NT.x64.Checked
```

Note that this will replace the coreclr used in the build, and because `-test` is passed, will also run the tests.

## Replace CoreCLR after building CoreFX normally

Do the following:

1. Build the CoreFX repo, but don't build tests yet.

```
f:\git\runtime> src\libraries\build.cmd -configuration Release -arch x64 -restore -build
```

This creates a "testhost" directory with a subdirectory that includes the coreclr bits, e.g., `f:\git\runtime\artifacts\bin\testhost\netcoreapp-Windows_NT-Release-x64\shared\Microsoft.NETCore.App\3.0.0`.

2. Copy the contents of the CoreCLR build you wish to test into the CoreFX runtime
folder created in step #1.

```
f:\git\runtime> copy f:\git\runtime\artifacts\bin\coreclr\Windows_NT.x64.Checked\* f:\git\runtime\artifacts\bin\testhost\netcoreapp-Windows_NT-Release-x64\shared\Microsoft.NETCore.App\3.0.0
```

3. Optionally, create a script that contains any environment variables you want to set when running each CoreFX test. Disabling TieredCompilation or setting a JIT stress mode is a common case. E.g.,

```
f:\git\runtime> echo set COMPlus_TieredCompilation=0>f:\git\runtime\SetStressModes.bat
```

4. Build and run the CoreFX tests. Optionally, pass in a file that will be passed to xunit to provide extra xunit arguments. Typically, this is used to exclude known failing tests.

```
f:\git\runtime> src\libraries\build.cmd -configuration Release -arch x64 -buildtests -test /p:WithoutCategories=IgnoreForCI /p:PreExecutionTestScript=f:\git\runtime\SetStressModes.bat /p:TestRspFile=f:\git\runtime\src\coreclr\tests\CoreFX\CoreFX.issues.rsp
```

## Handling cross-compilation testing

The above instructions work fine if you are building and testing on the same machine,
but what if you are building on one machine and testing on another? This happens,
for example, when building for Windows arm32 on a Windows x64 machine,
or building for Linux arm64 on a Linux x64 machine (possibly in Docker).
In these cases, build all the tests, copy them to the target machine, and run tests
there.

To do that, remove `-test` from the command-line used to build CoreFX tests. Without `-test`,
the tests will be built but not run.

If using `run-corefx-tests.py`, pass the argument `-no_run_tests`.

After the tests are copied to the remote machine, you want to run them. Use one of the scripts
[tests\scripts\run-corefx-tests.bat](https://github.com/dotnet/runtime/blob/master/src/coreclr/tests/scripts/run-corefx-tests.bat) or
[tests\scripts\run-corefx-tests.sh](https://github.com/dotnet/runtime/blob/master/src/coreclr/tests/scripts/run-corefx-tests.sh)
to run all the tests (consult the scripts for proper usage). Or, run a single test as described below.

## Running a single CoreFX test assembly

Once you've built the CoreFX tests (possibly with replaced CoreCLR bits), you can also run just a single test. E.g.,

```
f:\git\runtime> cd f:\git\runtime\artifacts\bin\System.Buffers.Tests\netcoreapp-Release
f:\git\runtime\artifacts\bin\System.Buffers.Tests\netcoreapp-Release> RunTests.cmd -r f:\git\runtime\artifacts\bin\testhost\netcoreapp-Windows_NT-Release-x64
```

Alternatively, you can run the tests from from the test source directory, as follows:

```
f:\git\runtime> cd f:\git\runtime\src\System.Buffers\tests
f:\git\runtime\src\System.Buffers\tests> dotnet msbuild /t:Test /p:ForceRunTests=true;ConfigurationGroup=Release
```

# Using a published snapshot of CoreFX tests

The corefx official build system publishes a set of corefx test packages for consumption
by the coreclr CI. You can use this set of published files, but it is complicated, especially
if you wish to run more than one or a few tests.

The process builds a "test host", which is a directory layout like the dotnet CLI, and uses that
when invoking the tests. CoreFX product packages, and packages needed to run CoreFX tests,
are restored, and the CoreCLR to test is copied in.

For Windows:

1. `.\src\coreclr\build.cmd <arch> <build_type> skiptests` -- build the CoreCLR you want to test
2. `.\src\coreclr\build-test.cmd <arch> <build_type> buildtesthostonly` -- this generates the test host

For Linux and macOS:

1. `./src/coreclr/build.sh <arch> <build_type> skiptests`
2. `./src/coreclr/build-test.sh <arch> <build_type> generatetesthostonly`

The published tests are summarized in a `corefx-test-assets.xml` file that lives here:

```
https://dotnetfeed.blob.core.windows.net/dotnet-core/corefx-tests/$(MicrosoftPrivateCoreFxNETCoreAppVersion)/$(__BuildOS).$(__BuildArch)/$(_TargetGroup)/corefx-test-assets.xml
```

where `MicrosoftPrivateCoreFxNETCoreAppVersion` is defined in `eng\Versions.props`. For example:

```
https://dotnetfeed.blob.core.windows.net/dotnet-core/corefx-tests/4.6.0-preview8.19326.15/Linux.arm64/netcoreapp/corefx-test-assets.xml
```

This file lists all the published test assets. You can download each one, unpack it, and
then use the generated test host to run the test.

Here is an example test file:
```
https://dotnetfeed.blob.core.windows.net/dotnet-core/corefx-tests/4.6.0-preview8.19326.15/Linux.arm64/netcoreapp/tests/AnyOS.AnyCPU.Release/CoreFx.Private.TestUtilities.Tests.zip
```

There is no automated way to download, unpack, and run all the tests. If you wish to run all the tests, one of the methods in the "Building CoreFX against CoreCLR"
section should be used instead, if possible.

# CoreFX test exclusions

The CoreCLR CI system runs CoreFX tests against a just-built CoreCLR. If tests need to be
disabled due to transitory breaking change, for instance, update the
[test exclusion file](https://github.com/dotnet/runtime/blob/master/src/coreclr/tests/CoreFX/CoreFX.issues.rsp).
