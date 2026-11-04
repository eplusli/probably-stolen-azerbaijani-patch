# Probably Stolen — Azərbaycan dili yaması

Oyunu gündəlik Azərbaycan dilində oynamaq üçün hazırlanmış qeyri-rəsmi tərcümə.

**Uyğun oyun versiyası: 100-REV6 · Yama versiyası: 1.0.0 · Windows 10/11 (64-bit)**

## Yüklə və quraşdır

1. [Son buraxılışdan](https://github.com/eplusli/probably-stolen-azerbaijani-patch/releases/latest) **ProbablyStolen-Azerbaijani-Setup-v1.0.0.exe** faylını yüklə.
2. Oyunu bağla və quraşdırıcını aç.
3. `Probably Stolen.exe` olan qovluğu seç, sonra **Install** düyməsinə bas.
4. Oyunun dil menyusundan **Azərbaycan dili** seç. Yama əvvəlki English seçimini əvəz edir.

Quraşdırıcı faylları yoxlayır və orijinalların ehtiyat nüsxəsini oyunun içindəki `_AzerbaijaniPatch_Backup_100-REV6` qovluğunda saxlayır. Yaddaş fayllarına toxunmur. Yamanı silmək üçün eyni quraşdırıcıda **Restore original** seç.

## Nələr tərcümə olunub?

- 14 dil cədvəlindəki bütün **6 698 mətn**.
- Əlavə menyu, səhnə, dialoq və əşya məlumatları.
- Azərbaycan hərfləri üçün şrift dəstəyi.

Şəxs və marka adları uyğun yerlərdə saxlanılıb. Rəsmlərin içindəki yazılar və DLC bu yamaya daxil deyil. Menyular, ayarlar, yeni oyun ekranları, mağaza, başlanğıc dialoqu və oyun içindəki sənəd yoxlanılıb. Bütün hekayə yolları sona qədər oynanılmayıb.

## English

Unofficial everyday Azerbaijani translation for **Probably Stolen, build 100-REV6**. Download the standalone Windows installer from [Releases](https://github.com/eplusli/probably-stolen-azerbaijani-patch/releases/latest), close the game, select the folder containing `Probably Stolen.exe`, and click **Install**. Select **Azərbaycan dili** in the game; this replaces its English language slot.

The installer verifies file hashes, backs up originals, supports restoring them, and leaves saves untouched. It refuses unsupported or modified game files. Existing installations of this same patch are recognized when verified backups are present. Keep the backup folder to restore the original game.

This repository supplies translation patch data and installer source. A separate copy of the game is required. Game updates may require a new patch. Game artwork, audio and DLC are excluded.

## Build

Requires Windows 10/11 with .NET Framework 4.8 or newer (normally included on current Windows installations).

To build:

```powershell
powershell -NoProfile -File .\build.ps1
```

The build embeds compressed `patch/manifest.json` and `patch/payload.bin` in a standalone Windows executable. No Python, Unity editor, or additional runtime installation is required for players.

Installer code is under the MIT license. Game content and trademarks belong to their respective owners.
