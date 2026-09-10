name : PollenRobotics.Net SDK for .NET
description: Unofficial .NET robotics SDK for PollenRobotics, berisi SDK .NET untuk robot:

1. Reachy Mini - https://github.com/pollen-robotics/reachy_mini reachymini 
2. Micro Duck - https://github.com/pollen-robotics/microduck microduck 
3. Reachy 2 - https://github.com/pollen-robotics/reachy2-sdk

Lengkapi dengan gallery apps built with Avalonia UI (aneka use case + sample code) - UI UX dengan skill frontend-design, dokumentasi lengkap, nuget
----
Tambahkan 2 tools berikut:

1. Buatkan simulator robot berbasis Avalonia+Blazor+Three JS
  - Bisa memilih jenis robotnya, Start/Stop
  - ada tampilan 3D-nya (cari model-nya di internet atau buat semirip mungkin)
  - ada logs panel untuk monitoring aplikasi / system
  - ada status panel robot
  - Compatible dengan SDK yang dibuat
  - Bikin UI UX yang keren, modern, easy to use, dengan theme dark/light dengan bantuan skill frontend-design

2. Buatkan PollenRobotics Robot Wizard, yaitu pembuat aplikasi robot dengan bantuan LLM
  - bentuknya seperti code editor
  - Menu: New Project, Close Project, beberapa menu terkait code editor: cut/copy/paste, go to line, search, replace, line number toggle. Build, Run, Deploy, tersedia Logs Panel dan Status Bar. 
  - Project yang dibuat bisa desktop, console, web, atau di embed ke robot
  - Ketika New project ada 2 pilihan: Blank dan From Template (buatkan banyak template aplikasi robot dengan berbagai use case)
  - About
  - Bikin UI UX yang keren, modern, easy to use, dengan theme dark/light dengan bantuan skill frontend-design
  - Tersedia Chat Bot Code Asisstant yang membantu membuatkan aplikasi robot dengan bantuan natural language  
  - Namanya 'Jack The Code Bender'
  - Bentuknya adalah Chat Panel di sebelah kanan code editor dengan tampilan yang keren, multi session (create/delete), reset session, bisa attach gambar (diupload lalu url-nya di jadikan image content) dan dokumen (di upload dan disertakan linknya ke text message).
  - Chat Panel bisa di show/hide
  - System Prompt (persona), temperature, model dan setting lainnya di simpan di app.config
  - Menggunakan Semantic Kernel Library dengan dukungan model: Open AI, Anthropic, Gemini, Ollama (bisa pilih)
  - Tambahkan beberapa common functions (kernel functions) yang diperlukan termasuk query ke tavily (search internet), scrap page url, baca file dari url, cek tanggal, Waktu, math calculation, dan beberapa function yang diperlukan lainnya
  - Tambahkan functions untuk mengenerate code aplikasi dengan memanfaatkan PollenRobotics SDK C# dan .NET Library berdasarkan instruksi user baik frontend atau backend code dengan c#
  - Bisa render chat thread dengan mark down dengan baik (baik table, media (image, video, audio), code, dan lainnya dengan baik)
  - Berikan contoh-contoh template prompt untuk generate aplikasi yang menarik (bikinkan yang banyak)
  - aplikasi bisa di run di robot langsung atau di simulator

Notes:
- gunakan .NET 10
- optimasi koding agar ringan dan cepat
- gunakan naming convention standard c#
- dokumentasi lengkap di folder docs
- readme dalam bahasan Indonesia dan English
- support ML: bisa gunakan scisharp, torchsharp, ML.Net
- support LLM: semantic kernel dengan pilihan model: OpenAI, Anthropic, Gemini, Ollama
- Progress.md untuk tracking development, PLAN.md untuk roadmap pengembangan
- jika ada hal-hal yang penting perlu ditambahkan, silakan ditambahkan langsung biar lengkap.
- untuk simulatornya dibuat model robotnya semirip mungkin, manfaatkan skill three-js-builder
- di aplikasi dan dokumentasi tambahkan informasi dibuat oleh Gravicode Studios dipimpin Kang Fadhil
- ujicoba dengan LLM real bisa gunakan api dari 'C:\Users\mifma\Documents\CodeSandbox\testkey.txt'
- untuk publish nuget, api key ada di 'C:\Users\mifma\Documents\CodeSandbox\PackageCredentials.txt'