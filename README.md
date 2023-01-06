# Quicker Configurator

Based on the Design Automation tutorial sample from learnforge.autodesk.io 

In order to use the "Keep workitem running (max 1 minute)" option in the sample, you need to have a **Forge subscription** and then reach out to **forge.help@autodesk.com** to get **Open Network** enabled for yor **Forge app**. See this for more info: [Open Network in Preview](https://aps.autodesk.com/blog/open-network-preview)

## Description

It includes 2 modules:

- .NET Framework plugin **[Inventor](UpdateIPTParam/)**. See each readme for plugin details.
- .NET Core web interface to invoke Design Automation v3 and show results. See [readme](forgesample/) for more information.

The `designautomation.sln` includes the bundle and the webapp. The `BUILD` action copy all files to the bundle folder, generate a .zip and copy to the webapp folder. 
