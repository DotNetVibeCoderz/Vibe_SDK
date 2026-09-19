Unofficial *TypeSafe .NET SDK*, modeled after the official Python SDK, with added sample applications, notebooks, and simulators to expose all functionalities. This design ensures parity with the Python version while leveraging .NET’s ecosystem for console, desktop, and web apps.

---

 🔹 Core SDK Features
- TypeSafeClient: .NET wrapper for the TypeSafe API, supporting async/await patterns.  
- Choice abstraction: strongly typed enums for classification tasks.  
- SystemOne API: entry point for structured decision-making workflows.  
- Configuration: environment-based API key injection via `IConfiguration` and `Options`.  
- Error handling: typed exceptions for API/network errors.  
- Logging integration: hooks into `Microsoft.Extensions.Logging`.  

---

 🔹 Developer Tools
- CLI tools: `dotnet typesafe` for model testing, schema validation, and API calls.  
- Template projects: scaffolding for console, Blazor Server, Avalonia UI, and ASP.NET APIs.  
- VSCode extensions: syntax highlighting and snippets for TypeSafe SDK usage.  
- NuGet package: distributed via NuGet with semantic versioning.  

---

 🔹 Sample Applications
- Console app: ticket classification demo.  
  ```csharp
  using TypeSafeSdk;
  var client = new TypeSafeClient();
  var response = await client.SystemOneAsync(
      new { document = "I was charged twice. Please fix this ASAP." },
      new { category = Choice.Create("billing","technical","other") }
  );
  Console.WriteLine(response.Choices["category"].Choice);
  ```
- Blazor web app: interactive UI for categorizing customer support tickets.  
- Avalonia UI desktop app: drag-and-drop document classification.  
- ASP.NET API: REST wrapper exposing TypeSafe endpoints for enterprise integration.  

---

 🔹 Notebook & Simulator
- Polyglot notebook: Jupyter/Polyglot Notebook with C# kernel to run TypeSafe demos inline.  
- Simulator: mock environment for testing workflows without hitting live API.  
  - Simulates responses for billing/technical/other categories.  
  - Useful for unit testing and CI/CD pipelines.  

---

 🔹 Documentation & Ecosystem
- Full docs: XML comments + Markdown guides.  
- Sample gallery: Avalonia UI, Blazor, Avalonia UI, and console demos.  
- Integration examples: connecting with EF Core, Azure Functions, and workflow engines.  

---
 🌐 Tools

- Tools berupa aplikasi dengan Avalonia UI Bernama TypeSafe App Generator (TypeSafeAppGen) bentuknya seperti code editor yang memiliki fungsi generate app with prompt dengan bantuan LLM menggunakan library semantic kernel, LLM yang disupport: OpenAI, Claude, Gemini, Ollama, settingnya (model, api key, endpoint, temperature, system prompt) disimpan di app.config. 
- Nama AI Assistant: Jack - The Code Bender
- Buatkan kernel functions yang diperlukan agar assisten AI-nya bisa membuatkan aplikasi dengan benar baik UI dan Backend Code-nya, kasih common functions juga untuk SearchInternet (tavily), ScrapeWebPage, MathCalculation, Check Date and Time, dan fungsi lain yang diperlukan. 
- Panel chat ada di sebelah kanan code editor, bisa attach gambar, bisa di resize width-nya dan hide/show, send chat bisa dengan Ctrl+Enter atau klik button send, ada button untuk clear chat thread, Model LLM bisa dipilih dibagian atas Chat Panel 
- Di tengah ada code editor, lengkap dengan line number, code highlight
- Di panel kiri ada code explorer seperti VSCode
- Pada menu dan toolbar terdapat fungsi: New Project (Folder), Open Project/File, Close Project, Go To Line Number, Format Code, Build, Run, Deploy, Exit. 
- Create new project ada 2 pilihan: Blank dan From Template (buatkan berbagai template jenis aplikasi untuk 3D Grafik, Animasi, Game, Simulator, dsb dengan use case bermacam-macam). 
- Terdapat status bar dan logs panel di bagian bawah untuk memantau proses dan output. 
- Show/hide line number pada code editor. 
- Buatkan dengan UI dan UX modern dengan skill frontend-design. Semua konfigurasi disimpan di app.config dan bisa di ubah di UI. 

---

✅ With this design, the TypeSafe .NET SDK mirrors the Python SDK’s capabilities while adding rich tooling for .NET developers. The combination of sample apps, notebooks, and simulators ensures developers can explore every feature interactively.  

Notes:
- gunakan .NET 10
- Semua UI UX aplikasi buat yang keren dan user friendly dengan bantuan skill 'frontend-design'
- Untuk sample apps tipe desktop buat dengan Avalonia UI Multiplatform, untuk tipe web gunakan blazor server
- optimasi koding agar dapat performa terbaik dan memory efisien
- gunakan naming convention standard c#
- readme dan docs dalam bahasa Indonesia dan English
- dokumentasi lengkap di folder docs
- Progress.md untuk tracking development, PLAN.md untuk roadmap pengembangan
- jika ada hal-hal yang penting perlu ditambahkan, silakan ditambahkan langsung biar lengkap.
- di aplikasi dan dokumentasi tambahkan informasi dibuat oleh Gravicode Studios dipimpin Kang Fadhil
- untuk testing SDK bisa gunakan apikey di 'C:\Users\mifma\Documents\CodeSandbox\TypeSafeApiKey.txt'
- untuk publish nuget, api key ada di 'C:\Users\mifma\Documents\CodeSandbox\PackageCredentials.txt'
- ujicoba dengan LLM real bisa gunakan api dari 'C:\Users\mifma\Documents\CodeSandbox\testkey.txt'
- screenshot-screenshot tambahkan pada dokumentasi dan readme
