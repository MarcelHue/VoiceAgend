// Minimaler Service Worker — erfüllt nur die PWA-Installierbarkeit (Secure Context +
// registrierter SW mit fetch-Handler). Bewusst KEIN App-Shell-Caching: die App ist
// immer online (Transkription braucht den Server), Offline-Betrieb ist kein Ziel.
self.addEventListener('install', () => self.skipWaiting());
self.addEventListener('activate', (e) => e.waitUntil(self.clients.claim()));
self.addEventListener('fetch', () => { /* Passthrough — Browser holt normal aus dem Netz. */ });
