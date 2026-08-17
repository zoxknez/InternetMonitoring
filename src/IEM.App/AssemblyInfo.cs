using System.Runtime.CompilerServices;
using System.Windows;

[assembly: ThemeInfo(
    ResourceDictionaryLocation.None,            //where theme specific resource dictionaries are located
                                                //(used if a resource is not found in the page,
                                                // or application resource dictionaries)
    ResourceDictionaryLocation.SourceAssembly   //where the generic resource dictionary is located
                                                //(used if a resource is not found in the page,
                                                // app, or any theme specific resource dictionaries)
)]

// The named elements XAML generates are internal, so a test in another assembly cannot read
// what a dialog actually put on screen without this. Granted to the test project only, and
// deliberately: the alternative was widening the window's own API for the benefit of tests,
// which puts pressure on the design in the wrong direction.
[assembly: InternalsVisibleTo("IEM.App.Tests")]
