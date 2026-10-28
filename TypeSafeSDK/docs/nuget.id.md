# Publikasi ke NuGet

**Bahasa Indonesia** · [English](nuget.en.md)

SDK ini dirilis sebagai `Gravicode.TypeSafeSdk`. Id paket diberi awalan karena ini port **tidak resmi**: `TypeSafeSdk` tanpa awalan akan terbaca seperti paket resmi vendor, padahal bukan.

## Instalasi

```bash
dotnet add package Gravicode.TypeSafeSdk
```

```xml
<PackageReference Include="Gravicode.TypeSafeSdk" Version="1.2.0" />
```

Nama assembly dan root namespace tetap `TypeSafeSdk`, jadi pengguna cukup menulis `using TypeSafeSdk;`.

## Penomoran versi

`TypeSafeConstants.SdkVersion` dan `<Version>` pada `TypeSafeSdk.csproj` adalah angka yang sama, dan angka itu juga dikirim pada header `X-TypeSafe-SDK`. Naikkan keduanya bersamaan, kalau tidak header akan berbohong tentang build mana yang mengirim request.

Berlaku semantic versioning:

- **patch** — perbaikan tanpa perubahan permukaan API
- **minor** — API baru, pemanggilan lama tetap bisa dikompilasi
- **major** — apa pun yang memaksa pengguna mengubah kodenya

## Pack

```bash
dotnet pack TypeSafeSdk/TypeSafeSdk.csproj -c Release -o artifacts
```

Perintah ini menghasilkan `artifacts/Gravicode.TypeSafeSdk.<versi>.nupkg` beserta paket simbol `.snupkg`. Paket menyertakan README, ekspresi lisensi MIT, URL repositori, dan source link deterministik sehingga pengguna bisa menelusuri kode SDK dari debugger mereka.

Sebelum push, pastikan isi paket sesuai harapan:

```bash
dotnet nuget verify artifacts/Gravicode.TypeSafeSdk.1.2.0.nupkg
unzip -l artifacts/Gravicode.TypeSafeSdk.1.2.0.nupkg
```

## Rilis dari CI

Rilis biasanya lewat GitHub Actions, yang membaca secret repository `NUGET_API_KEY`. Dua workflow ada di root monorepo:

- `.github/workflows/typesafe-sdk-ci.yml` — pada setiap push atau PR yang menyentuh `TypeSafeSDK/**`: build seluruh solution dan menjalankan semua test di Windows (board games adalah WPF), menjalankan test SDK dan App Generator di Ubuntu, lalu mem-pack prerelease `1.2.0-ci.<run>` sebagai artifact build. Tidak ada yang di-push.
- `.github/workflows/typesafe-sdk-publish.yml` — pada tag `typesafe-sdk-v*`, atau manual dari tab Actions: test, pack, lalu push ke nuget.org.

```bash
git tag typesafe-sdk-v1.2.0
git push origin typesafe-sdk-v1.2.0
```

Tag diberi awalan karena Vibe_SDK memuat beberapa SDK, dan tag rilis SDK lain tidak boleh ikut mempublish paket ini. Workflow publish menolak berjalan kecuali tag, `<Version>`, dan `TypeSafeConstants.SdkVersion` bernilai sama, sehingga header tidak pernah berbohong soal build-nya. Jalankan manual dengan `dry_run` dicentang untuk mem-pack dan mengunggah paket sebagai artifact tanpa push — cara berlatih rilis.

Paket menyertakan ikon `TypeSafeSdk/icon.png` (256×256): `<T>` generik di atas strip probabilitas dengan palet Jev Gallery.

## Push manual

Hanya diperlukan bila CI tidak tersedia.

API key disimpan di luar repositori, pada `PackageCredentials.txt`. Jangan pernah menempelkannya ke perintah yang akan tercatat di history shell atau ikut ter-commit.

```powershell
$key = (Select-String -Path 'C:\Users\mifma\Documents\CodeSandbox\PackageCredentials.txt' -Pattern 'ApiKey\s*=\s*(.+)').Matches.Groups[1].Value.Trim()
dotnet nuget push artifacts\Gravicode.TypeSafeSdk.1.2.0.nupkg --source https://api.nuget.org/v3/index.json --api-key $key
```

Push bersifat permanen. nuget.org memungkinkan sebuah versi di-*unlist* agar tidak muncul di pencarian, tetapi versi itu tidak pernah bisa dihapus dan nomor versi yang sama tidak pernah bisa dipakai ulang. Periksa nomor versi dua kali sebelum menjalankan perintah.

## Checklist rilis

1. `dotnet test TypeSafeSDK.slnx` hijau.
2. `TypeSafeConstants.SdkVersion` sama dengan `<Version>`.
3. Versi tersebut belum ada di nuget.org.
4. `docs/` dan `README.md` menjelaskan versi yang dirilis.
5. `Progress.md` mencatat rilisnya.
6. Pack, periksa isinya, baru push.

Dibuat oleh **Gravicode Studios**, dipimpin Kang Fadhil.
