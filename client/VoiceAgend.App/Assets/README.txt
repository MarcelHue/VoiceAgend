Hier müssen zwei .ico-Dateien liegen, damit das Tray-Icon angezeigt wird:

  TrayIdle.ico       — Standardzustand (kein Recording)
  TrayRecording.ico  — während der Aufnahme

Quick-Setup:
  1) Beliebige zwei einfarbige PNGs (32x32) machen — z.B. ein graues und ein rotes Mikrofon
  2) Online-Konverter wie https://icoconvert.com nutzen
  3) Ergebnis hier ablegen.

Oder erstmal aus dem Windows-System-Cache kopieren:
  copy %SystemRoot%\System32\imageres.dll  (enthält viele Icons)
  oder mit 7-Zip *.dll öffnen, geeignete .ico extrahieren.

Provisorisch: irgendein .ico aus dem Web hier ablegen und beide Dateien identisch
benennen — das Build wird dann zumindest grün.
