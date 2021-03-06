# Quicker Configurator - Inventor AppBndle

![Platforms](https://img.shields.io/badge/Plugins-Windows-lightgray.svg)
![.NET](https://img.shields.io/badge/.NET%20Framework-4.7-blue.svg)
[![Inventor](https://img.shields.io/badge/Inventor-2020-lightblue.svg)](http://developer.autodesk.com/)

![Basic](https://img.shields.io/badge/Level-Basic-blue.svg)

# Description

Based on the Design Automation for Inventor AppBundle from the learnforge.autdoesk.io site 

Inventor plugin that updates 3 params of our assembly, generates a preview image and provides transformation values of each component in the assembly

# Setup

## Prerequisites

1. **Visual Studio** 2019
2. **Inventor** 2020 required to compile changes into the plugin

## References

This Inventor plugin requires **Autodesk.Inventor.Interop** reference, which should resolve from the system GAC. If not, right-click on **References**, then **Add** > **References**, then add it from the follwing folder: `C:\Program Files\Autodesk\Inventor 2019\Bin\Public Assemblies`.

![](../media/inventor/references.png) 

## Build

The `After Build` event of the project is zipping up the neccessary files and places the zip in the `forgesample\wwwroot\bundles` folder so that the web app will be able to access it and upload it when using the `Configure` dialog - see [UpdateIPTParam.csproj](./UpdateIPTParam.csproj#L85) 

## Debug Locally

Please review this section of the [My First Plugin Tutorial](https://knowledge.autodesk.com/support/inventor-products/learn-explore/caas/simplecontent/content/lesson-2-programming-overview-autodesk-inventor.html).

# Further Reading

- [My First Inventor Plugin](https://knowledge.autodesk.com/support/inventor-products/learn-explore/caas/simplecontent/content/my-first-inventor-plug-overview.html)
- [Inventor Developer Center](https://www.autodesk.com/developer-network/platform-technologies/inventor)

## License

This sample is licensed under the terms of the [MIT License](http://opensource.org/licenses/MIT). Please see the [LICENSE](LICENSE) file for full details.

## Written by

Adam Nagy \
[Developer Advocacy and Support](http://forge.autodesk.com)