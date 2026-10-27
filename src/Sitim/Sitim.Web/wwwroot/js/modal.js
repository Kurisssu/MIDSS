// SitimModal now delegates to Radzen's DialogService, which handles open/close,
// backdrop, focus trap and nested popup positioning natively. The legacy
// <dialog>.showModal() shim previously here was removed because it forced the
// modal into the browser's top layer, where Radzen's dropdown popups (rendered
// at body root, not top layer) ended up drawn underneath.
//
// File kept as an empty stub so any cached <script src="js/modal.js"> tag in
// browsers picking up the older index.html doesn't 404.
