var viewer;

function launchViewer(data, objectNameBase) {
    var options = {
        env: 'AutodeskProduction',
        getAccessToken: getForgeToken
    };

    if (viewer) {
        viewer.tearDown();
        viewer.setUp(viewer.config);

        loadModels(data, objectNameBase)
    } else {
        Autodesk.Viewing.Initializer(options, () => {
            viewer = new Autodesk.Viewing.GuiViewer3D(document.getElementById('forgeViewer'), { extensions: ['Autodesk.DocumentBrowser'] });
            viewer.start();
            loadModels(data, objectNameBase)
        });
    }
}

function loadModels(data, objectNameBase) {
    data.components.forEach(component => {
        var objectName = objectNameBase + component.fileName
        var documentId = 'urn:' + btoa(objectName);
        Autodesk.Viewing.Document.load(documentId, onDocumentLoadSuccess(component), onDocumentLoadFailure);
    }) 
}

function onDocumentLoadSuccess(component) {
    return function (doc) {
        var viewables = doc.getRoot().getDefaultGeometry();
        let opt = {
            placementTransform: THREE.Matrix4()
        }
        viewer.loadDocumentNode(doc, viewables, opt).then(i => {
            // documented loaded, any action?
        });
    }  
}

function onDocumentLoadFailure(viewerErrorCode) {
    console.error('onDocumentLoadFailure() - errorCode:' + viewerErrorCode);
}

function getForgeToken(callback) {
    fetch('/api/forge/oauth/token').then(res => {
        res.json().then(data => {
            callback(data.access_token, data.expires_in);
        });
    });
}
