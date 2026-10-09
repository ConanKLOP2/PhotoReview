---
id: Q-FMT-WEBP-HEIC
order: 143
summary: |-
  WebP + HEIC/HEIF supported through the Windows codecs (WIC), no bundled libheif/libde265 (reverses Q-R52 for these two formats only, user request 2026-10-09); setting WebpHeicSupportEnabled (default on); a missing Store codec is a localized per-file error with install guidance, logged once; animated WebP shows frame 1; JXL/AVIF/PSD stay deferred.
---

# Q-FMT-WEBP-HEIC â€” WebP vÃ  HEIC/HEIF qua codec cá»§a Windows (2026-10-09)

**Bá»‘i cáº£nh.** NgÆ°á»i dÃ¹ng khÃ´ng xem Ä‘Æ°á»£c áº£nh HEIC (iPhone) vÃ  muá»‘n xem WebP (áº£nh táº£i tá»« web). [Q-R52](Q-R41-Q-R52-user-feedback.md)
Ä‘Ã£ DECLINED "Ä‘á»‹nh dáº¡ng má»›i" cho tá»›i khi cÃ³ quyáº¿t Ä‘á»‹nh kiáº¿n trÃºc riÃªng (thÆ° viá»‡n, giáº¥y phÃ©p, káº¿ hoáº¡ch test). NgÃ y 2026-10-09
ngÆ°á»i dÃ¹ng yÃªu cáº§u láº¡i; quyáº¿t Ä‘á»‹nh nÃ y Ä‘áº£o Q-R52 **chá»‰ cho WebP vÃ  HEIC/HEIF**. JXL, AVIF, PSD váº«n Ä‘á»ƒ sau.

## Quyáº¿t Ä‘á»‹nh

| Má»¥c | Ná»™i dung |
|---|---|
| ÄÆ°á»ng giáº£i mÃ£ | Windows Imaging Component (WIC) cÃ³ sáºµn: WebP qua "Microsoft Webp Decoder" (Windows 11 / gÃ³i Store "WebP Image Extensions"); HEIC/HEIF qua "HEIF Image Extensions" + "HEVC Video Extensions" (Store). LuÃ´n Ä‘i `WicDirectDecoder` (+ fallback WPF nhÆ° má»i backend), báº¥t ká»ƒ backend ngÆ°á»i dÃ¹ng chá»n (`WebpHeicRoutingDecoder`). |
| KhÃ´ng Ä‘Ã³ng gÃ³i | KhÃ´ng libheif, libde265, libwebp hay DLL native má»›i; `THIRD-PARTY-NOTICES.md` khÃ´ng Ä‘á»•i. |
| Danh sÃ¡ch Ä‘uÃ´i | `.webp`, `.heic`, `.heif` (`ImageFileTypes.WebpHeicExtensions`); `.hif`/`.avif`/`.heics`/`.jxl` khÃ´ng Ä‘Æ°á»£c liá»‡t kÃª dÃ¹ codec HEIF/JXL cá»§a Windows nháº­n chÃºng. |
| Setting | `WebpHeicSupportEnabled`, máº·c Ä‘á»‹nh Báº¬T (cáº£ config cÅ© khÃ´ng cÃ³ trÆ°á»ng nÃ y), CÃ i Ä‘áº·t > "WebP vÃ  HEIC/HEIF"; Ä‘á»•i giÃ¡ trá»‹ thÃ¬ náº¡p láº¡i thÆ° má»¥c Ä‘ang má»Ÿ (giá»‘ng `RawSupportEnabled`). Cá»­a sá»• CÃ i Ä‘áº·t hiá»‡n dÃ²ng tráº¡ng thÃ¡i codec cá»§a mÃ¡y. |
| Probe lÃºc cháº¡y | `WicCodecAvailability`: liá»‡t kÃª decoder WIC (`CreateComponentEnumerator`, tháº¥y cáº£ codec gÃ³i Store) + há»i Media Foundation cÃ³ decoder HEVC (`MFTEnumEx`). Cháº¡y má»™t láº§n/tiáº¿n trÃ¬nh, lÆ°á»i (láº§n Ä‘áº§u gáº·p Ä‘Æ°á»ng dáº«n WebP/HEIC hoáº·c má»Ÿ CÃ i Ä‘áº·t) â€” khÃ´ng cháº¡m Ä‘Æ°á»ng khá»Ÿi Ä‘á»™ng. CÃ i codec khi app Ä‘ang cháº¡y: cáº§n khá»Ÿi Ä‘á»™ng láº¡i. |
| MÃ¡y thiáº¿u codec | File VáºªN Ä‘Æ°á»£c liá»‡t kÃª (ngÆ°á»i dÃ¹ng tháº¥y áº£nh iPhone cá»§a mÃ¬nh vÃ  Ä‘Æ°á»£c báº£o cáº§n cÃ i gÃ¬). Khi hiá»ƒn thá»‹: lá»—i `MissingImageCodecException` (má»™t `NotSupportedException`) cÃ³ cÃ¢u hÆ°á»›ng dáº«n Ä‘Ã£ dá»‹ch (en/vi) "cÃ i ... tá»« Microsoft Store rá»“i khá»Ÿi Ä‘á»™ng láº¡i". Bá»‹ tá»« chá»‘i trÆ°á»›c má»i láº§n Ä‘á»c Ä‘Ä©a; preload khÃ´ng prefetch byte cá»§a file Ä‘Ã³, khÃ´ng ghi lá»—i tá»«ng file (router ghi log má»™t láº§n cho má»—i Ä‘á»‹nh dáº¡ng). CÃ¡c áº£nh khÃ¡c duyá»‡t bÃ¬nh thÆ°á»ng. |
| Setting táº¯t | File khÃ´ng Ä‘Æ°á»£c liá»‡t kÃª; náº¿u váº«n tá»›i decoder (kÃ©o tháº£ cÅ©, Undo) thÃ¬ lá»—i Ä‘Ã£ dá»‹ch "WebP/HEIC Ä‘ang táº¯t". Undo khÃ´i phá»¥c file WebP/HEIC báº¥t ká»ƒ setting (trÃ¡nh trÃ´ng nhÆ° Undo há»ng). |
| HÆ°á»›ng áº£nh / alpha / Ä‘á»™ng | HÆ°á»›ng EXIF: chuá»—i Ä‘á»c metadata sáºµn cÃ³ cá»§a WicDirect (`System.Photo.Orientation`). Alpha: WebP trong suá»‘t ra Pbgra32 nhÆ° PNG; cache Ä‘Ä©a JPEG tá»« chá»‘i áº£nh cÃ³ pixel trong suá»‘t (IMG-01/Q-R7), áº£nh Ä‘á»¥c Ä‘Æ°á»£c cache bÃ¬nh thÆ°á»ng. WebP Ä‘á»™ng: chá»‰ khung Ä‘áº§u. `ImageCacheKey` vÃ  Ä‘á»‹nh dáº¡ng `PreviewCacheFile` khÃ´ng Ä‘á»•i. |

## VÃ¬ sao WIC, khÃ´ng libheif

- **PhÃ¡p lÃ½/báº±ng sÃ¡ng cháº¿:** HEIC dÃ¹ng HEVC (H.265), cÃ³ nhiá»u nhÃ³m báº±ng sÃ¡ng cháº¿ (MPEG LA/Access Advance/Velos). Tá»± phÃ¢n phá»‘i
  libde265 (LGPL) + libheif (LGPL) trong báº£n build Windows kÃ©o theo rá»§i ro báº£n quyá»n sÃ¡ng cháº¿ HEVC cho ngÆ°á»i phÃ¢n phá»‘i vÃ  nghÄ©a vá»¥
  LGPL (cho phÃ©p thay DLL, kÃ¨m notice). Codec cá»§a Microsoft Ä‘Æ°á»£c ngÆ°á»i dÃ¹ng tá»± cÃ i tá»« Store, giáº¥y phÃ©p HEVC Ä‘i kÃ¨m gÃ³i Ä‘Ã³.
- **KÃ­ch thÆ°á»›c/báº£o trÃ¬:** khÃ´ng thÃªm DLL native, khÃ´ng pin SHA/fetch script, khÃ´ng cáº­p nháº­t báº£o máº­t cho parser HEIF cá»§a bÃªn thá»© ba.
- **Nháº¥t quÃ¡n:** ADR 0001 Ä‘Ã£ chá»n WIC lÃ m backend máº·c Ä‘á»‹nh; WebP/HEIC Ä‘i cÃ¹ng pipeline (ICCâ†’sRGB, xoay EXIF, pre-scale, premultiplied alpha).

## Háº¡n cháº¿ (Ä‘Ã£ cháº¥p nháº­n)

- Phá»¥ thuá»™c mÃ¡y ngÆ°á»i dÃ¹ng: HEIC cáº§n **cáº£** "HEIF Image Extensions" **vÃ ** "HEVC Video Extensions" (gÃ³i HEVC cÃ³ thá»ƒ máº¥t phÃ­;
  má»™t sá»‘ mÃ¡y cÃ³ báº£n "from Device Manufacturer" miá»…n phÃ­). WebP cÃ³ sáºµn trÃªn Windows 11; Windows 10 cáº§n gÃ³i "WebP Image Extensions".
- MÃ¡y dev (2026-10-09): WebP decoder **cÃ³**; HEIF decoder **cÃ³**; decoder HEVC **khÃ´ng** (MFTEnumEx tháº¥y AV1/H.264 nÃªn viá»‡c
  dÃ² gÃ³i Store hoáº¡t Ä‘á»™ng). VÃ¬ váº­y giáº£i mÃ£ HEIC tháº­t vÃ  hÆ°á»›ng xoay HEIC (irot/imir vs EXIF) **chÆ°a kiá»ƒm Ä‘Æ°á»£c trÃªn mÃ¡y nÃ y**;
  test `Heic_RealSample_*` (Category=Native) cháº¡y khi cÃ³ codec + biáº¿n `PHOTOREVIEW_HEIC_SAMPLE`. Cáº§n kiá»ƒm tra báº±ng máº¯t vá»›i áº£nh
  iPhone dá»c sau khi cÃ i HEVC.
- WebP Ä‘á»™ng chá»‰ hiá»‡n khung Ä‘áº§u; khÃ´ng phÃ¡t hoáº¡t áº£nh.
- Thumbnail/benchmark: benchmark (`BenchmarkWindow`) khÃ´ng liá»‡t kÃª WebP/HEIC; "Open with" (script Ä‘Äƒng kÃ½) khÃ´ng Ä‘Äƒng kÃ½ Ä‘uÃ´i má»›i.

## Äá»ƒ sau

JXL (Windows cÃ³ "Microsoft JPEG XL Decoder" trÃªn má»™t sá»‘ báº£n), AVIF (codec HEIF + AV1 Video Extension), PSD: má»—i Ä‘á»‹nh dáº¡ng cáº§n
quyáº¿t Ä‘á»‹nh riÃªng (danh sÃ¡ch Ä‘uÃ´i, káº¿ hoáº¡ch test, hÃ nh vi khi thiáº¿u codec) theo cÃ¹ng khuÃ´n máº«u nÃ y.
