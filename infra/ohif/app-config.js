// Read the viewer token injected in the URL by MIDSS (e.g. ?viewerToken=xxx)
const _midssViewerToken = (new URLSearchParams(window.location.search)).get('viewerToken') || '';

window.config = {
  routerBasename: '/ohif',

  extensions: [],
  modes: [],

  showStudyList: false,
  investigationalUseDialog: { option: 'never' },

  studyPrefetcher: {
    enabled: false,
    maxNumPrefetchRequests: 0,
  },

  dataSources: [
    {
      friendlyName: 'MIDSS',
      namespace: '@ohif/extension-default.dataSourcesModule.dicomweb',
      sourceName: 'dicomweb',
      configuration: {
        name: 'midss',
        qidoRoot: '/midss-dicomweb',
        wadoRoot: '/midss-dicomweb',
        wadoUriRoot: '/midss-dicomweb',
        imageRendering: 'wadors',
        thumbnailRendering: 'wadors',
        enableStudyLazyLoad: true,
        omitQuotationForMultipartRequest: true,
        qidoSupportsIncludeField: false,
        supportsReject: false,
        supportsStow: false,
        // Pass the short-lived viewer token to the API DICOMweb proxy.
        requestOptions: {
          headers: {
            'X-Viewer-Token': _midssViewerToken,
          },
        },
      },
    },
  ],

  defaultDataSourceName: 'dicomweb',
};
