var viewer;
var counter = 0;

function launchViewer(data) {
    return new Promise(async (resolve, reject) => {
        var options = {
            env: 'AutodeskProduction',
            getAccessToken: getForgeToken
        };

        if (viewer) {
            viewer.tearDown();
            viewer.setUp(viewer.config);

            await loadModels(data)

            resolve()
        } else {
            Autodesk.Viewing.Initializer(options, async () => {
                viewer = new Autodesk.Viewing.GuiViewer3D(document.getElementById('forgeViewer'), { extensions: ['Autodesk.DocumentBrowser'] });
                viewer.start();
                await loadModels(data)

                resolve()
            });
        }
    })
}

function loadModels(data, objectNameBase) {
    return new Promise(async (resolve, reject) => {       
        console.log('loadModels()');

        for (key in data.components) {
            let component = data.components[key]
            var objectName = data.urnBase + component.fileName
            var documentId = 'urn:' + btoa(objectName);

            console.log('before promise, ' + component.fileName)
            await loadModel(documentId, component)
            console.log('after promise, ' + component.fileName)
        }
        
        console.log('All documents loaded');
        console.log("Setting camera")

        // doing this instead of "viewer.autocam.cube.cubeRotateTo('front top right')"
        // because that does it as a stransition, i.e. slow 
        viewer.navigation.setWorldUpVector(new THREE.Vector3(0, 0, 1)) 
        let pos = new THREE.Vector3(1,-1,1)
        let target = new THREE.Vector3(0,0,0)
        viewer.navigation.setView (pos, target)
        let upVector = new THREE.Vector3(0,0,1)
        viewer.navigation.setCameraUpVector (upVector)

        viewer.utilities.fitToView(true)

        resolve()
    })
}

function loadModel(documentId, component) {
    return new Promise((resolve, reject) => {
        let onDocumentLoadSuccess = (doc) => {
            console.log(`onDocumentLoadSuccess() - counter = ${counter}`);
            var s = component
            var viewables = doc.getRoot().getDefaultGeometry();
            let mx = new THREE.Matrix4()
            mx.fromArray(component.cells).transpose()
            let opt = {
                placementTransform: mx,
                globalOffset:{x:0,y:0,z:0},
                preserveView: true,
                keepCurrentModels: true
            }
            console.log(component.cells)
            console.log(opt)
            viewer.loadDocumentNode(doc, viewables, opt).then(i => {
                resolve()
            });
        }
        
        let onDocumentLoadFailure = (viewerErrorCode) => {
            console.error('onDocumentLoadFailure() - errorCode:' + viewerErrorCode);
            reject()
        }

        Autodesk.Viewing.Document.load(documentId, onDocumentLoadSuccess, onDocumentLoadFailure);
    })
}

function getForgeToken(callback) {
    fetch('/api/forge/oauth/token').then(res => {
        res.json().then(data => {
            callback(data.access_token, data.expires_in);
        });
    });
}
