# Quicker Configurator

Based on the Design Automation tutorial sample from learnforge.autodesk.io 

## Description

It includes 2 modules:

- .NET Framework plugin **[Inventor](UpdateIPTParam/)**. See each readme for plugin details.
- .NET Core web interface to invoke Design Automation v3 and show results. See [readme](forgesample/) for more information.

The `designautomation.sln` includes the bundle and the webapp. The `BUILD` action copy all files to the bundle folder, generate a .zip and copy to the webapp folder. It requires [7-zip](https://www.7-zip.org/) tool.
