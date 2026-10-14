# 악보 인식 가져오기 (PDF·그림 → 악보)

사용자 요구: 유튜브 영상의 악보(NWC 등으로 만든 화면을 캡처한 그림)와 인쇄 악보를 스캔한 PDF를 ScoreStudio 악보로 바꾼다. 악보 인식(OMR)은 공개 프로그램 **Audiveris**에 맡기고, ScoreStudio는 입력 준비, 실행, 결과 가져오기와 이어 붙이기를 맡는다.

## Audiveris
- 저장소: https://github.com/Audiveris/audiveris (AGPL-3.0). 이 문서를 쓸 때의 최신판은 5.11.0(2026년 7월)이다.
- Windows 설치 파일(`Audiveris-<버전>-windows-x86_64.msi`, 약 70MB)에 Java가 들어 있다. `winget install --exact --id audiveris.org.Audiveris`로도 설치한다. 기본 설치 위치는 `C:\Program Files\Audiveris\Audiveris.exe`다.
- ScoreStudio는 Audiveris를 **배포하지 않는다.** 사용자의 PC에 따로 설치된 Audiveris를 별도 프로그램으로 실행한다. 그래서 ScoreStudio 배포에 AGPL 의무가 생기지 않는다(`docs/THIRD_PARTY.md`).
- 명령: `Audiveris -batch -export -constant org.audiveris.omr.text.Language.defaultSpecification=<언어> -output <출력 폴더> -- <입력 파일…>`. 입력 하나마다 `<이름>.mxl`이 나오고, 악장이 여럿이면 `<이름>.mvt<N>.mxl`이 나온다. 실패한 입력은 `.mxl`이 나오지 않는다. 종료 코드는 실패해도 0일 수 있으므로 성공은 출력 파일로 판단한다.
- 글자 인식(가사, 제목)에 쓰는 Tesseract 언어 파일은 설치되어 있지 않다. 언어 파일은 `<설정 폴더>/tessdata/<언어>.traineddata`에 둔다. 설정 폴더는 Windows에서 `%APPDATA%\AudiverisLtd\audiveris\config`, Linux에서 `$XDG_CONFIG_HOME/AudiverisLtd/audiveris` 또는 `~/.config/AudiverisLtd/audiveris`다. 언어 파일은 Audiveris가 쓰는 Tesseract 4.1.0 판(`https://raw.githubusercontent.com/tesseract-ocr/tessdata/4.1.0/<언어>.traineddata`)을 쓴다. ScoreStudio는 한국어(`kor`)와 영어(`eng`) 파일이 없으면 받아 두고 `kor+eng`로 실행한다.

## 이 환경에서 확인한 사실 (Audiveris 5.11.0 Linux판, 2026-10-14)
ScoreStudio 샘플 악보를 그림으로 그려 Audiveris에 넣고 원본과 비교했다(음높이·길이 순서의 일치율, `scratchpad`의 비교 도구).

| 입력 | 결과 |
|---|---|
| 300dpi (오선 간격 약 20픽셀) | 빔·화음·피아노 보표 샘플 100%, 두 성부 93% |
| 150dpi (오선 간격 8~10픽셀, 유튜브 캡처 수준) | Audiveris가 "오선 간격이 너무 작다"며 **거절** |
| 150dpi를 2배로 키운 그림 | 300dpi와 같은 결과 |

결론: 해상도가 낮은 그림은 ScoreStudio가 오선 간격을 재서 미리 키운다. 한 쪽 처리에는 10~20초가 걸린다.

## 처리 순서
1. **입력 고르기**: 파일 > "PDF·그림에서 악보 인식...". PDF와 그림(PNG, JPG, BMP, GIF, TIFF)을 여러 개 고를 수 있다. 고른 순서(파일 이름 순)대로 한 악보로 이어 붙인다.
2. **Audiveris 찾기**: 설정에 저장한 경로, `%ProgramFiles%\Audiveris`, `%LocalAppData%\Programs\Audiveris`, scoop 폴더, PATH 순서로 찾는다. 없으면 설치 안내 창을 띄운다(winget으로 설치, 내려받기 페이지 열기, 위치 직접 지정).
3. **언어 파일**: `kor`, `eng`가 없으면 받는다. 받지 못하면 글자 없이 인식한다고 알리고 계속한다.
4. **그림 준비** (그림 입력만): 회색조로 바꾸고 오선 간격을 잰다. 간격이 18픽셀보다 작으면 20픽셀 이상이 되도록 정수배(최대 4배)로 키워 임시 PNG로 저장한다. 간격을 잴 수 없으면(오선이 한 줄뿐인 조각 등) 2배로 키운다. PDF는 그대로 넘긴다(Audiveris가 300dpi로 그림화한다).
5. **실행**: 모든 입력을 Audiveris 한 번으로 처리한다(Java 시작이 한 번). 진행 창에 Audiveris 로그를 보이고, 취소하면 Audiveris를 끝낸다. 시간 제한은 3분 + 입력당 3분이다.
6. **가져오기**: 출력 `.mxl`을 입력 순서(같은 입력 안에서는 악장 번호 순)로 MusicXML 가져오기로 읽는다. 인식하지 못한 입력은 경고로 알린다.
7. **이어 붙이기** (`ScoreMerge`): 첫 결과에 나머지 결과의 마디를 파트 순서대로 붙인다.
   - 파트 수가 다르면 첫 결과의 파트 수에 맞춘다. 모자란 파트는 그 마디를 마디 쉼표로 채우고, 남는 파트는 버린다(경고).
   - 이어 붙인 첫 마디에서 줄을 바꾼다. 마디 번호는 이어서 다시 매긴다.
   - **겹친 마디 지우기**: 영상 캡처는 앞 그림의 마지막 줄이 다음 그림의 첫 줄에 다시 나오기 쉽다. 앞 결과의 마지막 k마디와 다음 결과의 처음 k마디가 모든 파트에서 같으면(음 종류·음높이·길이·위치), 가장 큰 k(2 이상)만큼 다음 결과의 앞 마디를 버린다. 지운 개수를 알린다. 한 마디만 같은 경우는 반복일 수 있으므로 지우지 않는다.
8. **결과**: 이어 붙인 악보를 새 악보로 연다. 제목은 첫 결과의 제목, 없으면 첫 입력 파일 이름이다. 가져오기 경고와 함께 "인식 결과에는 틀린 곳이 있을 수 있습니다. 원본과 비교해 고치세요."를 알린다.

## 데이터와 구조
- 저장 형식은 바뀌지 않는다. 인식 결과는 MusicXML 가져오기와 같은 경로로 들어온다.
- Core(`ScoreStudio.Core/Recognition`, 리눅스에서 테스트):
  - `AudiverisLocator`: 후보 경로 목록과 찾기(파일 존재 확인 함수를 받아 테스트한다).
  - `AudiverisRunner`: 명령 인자 만들기, 프로세스 실행·취소·시간 제한, 출력 파일 모으기.
  - `OcrLanguages`: tessdata 폴더 위치, 빠진 언어, 내려받기.
  - `GrayImage`, `PngCodec`(8비트 PNG 읽기·회색조 PNG 쓰기), `StaffSpacing`(오선 간격 재기), `ImageScaling`(정수배 확대).
  - `ScoreMerge`: 이어 붙이기와 겹친 마디 지우기.
  - `RecognitionJob`: 위 순서를 묶는다. 그림 읽기는 `IImageReader`로 받아서, 창에서는 WPF 디코더(모든 그림 형식), 테스트에서는 `PngCodec`을 쓴다.
- WPF: 메뉴, Audiveris 설치 안내 창, 진행 창(취소), 결과 열기. Audiveris 경로는 프로그램 설정(`settings.json`)에 저장한다.

## 수용 기준
### 자동 테스트 (Core, 리눅스)
1. `StaffSpacing`: 오선 간격 8·12·20픽셀(선 두께 1~3픽셀, 음표·글자 잡음 포함)의 합성 그림에서 ±1픽셀로 잰다. 오선이 없는 그림은 null이다.
2. `ImageScaling`: 2배 확대한 그림의 크기가 두 배이고, 원래 검은 선이 같은 자리에 두 배 두께로 남는다. 간격 8 → 3배, 10 → 2배, 20 → 1배, 측정 실패 → 2배.
3. `PngCodec`: 회색조·RGB·RGBA·팔레트 PNG(모든 필터 종류)를 읽고, 쓴 PNG를 다시 읽으면 같다.
4. `AudiverisLocator`: 설정 경로가 우선이고, 없는 경로는 건너뛰며, 아무것도 없으면 null이다.
5. `AudiverisRunner`: 인자 순서(`-batch -export -constant … -output … -- 입력…`). 가짜 Audiveris 스크립트(출력 폴더에 `.mxl`을 쓰는 셸 스크립트)로 실행·출력 수집·실패 입력 보고·취소·시간 제한을 확인한다.
6. `ScoreMerge`: 마디 수와 번호, 줄 바꿈, 파트 수 차이(채우기·버리기), 겹친 마디 2개 지우기, 한 마디 겹침은 지우지 않기.
7. `OcrLanguages`: 운영체제별 폴더, 빠진 언어 목록, 내려받기(로컬 HTTP 서버로)와 실패 시 부분 파일을 남기지 않기.

### 실제 Audiveris로 확인 (Audiveris가 있을 때만, 환경 변수 `SCORESTUDIO_AUDIVERIS`)
8. `samples/omr`의 150dpi 캡처 두 장(빔 샘플, 두 성부 샘플을 그린 것)으로 전체 순서(준비→인식→가져오기→이어 붙이기)를 돌린다. 두 그림 모두 키워서 인식되고, 11마디 한 악보가 되며, 원본 두 샘플의 음표·쉼표(음높이와 길이)가 순서대로 90% 이상 맞는다. 처음 측정값은 95.3%(64개 중 61개, Audiveris 5.11.0 Linux판)다. 키우기를 끄면 이 테스트가 실패한다.
9. Windows CI: 최신 Audiveris msi와 언어 파일을 설치하고 8을 실행한다(`SCORESTUDIO_REQUIRE_AUDIVERIS=1`이라 건너뛰지 않는다). 일치율은 테스트 기록에 남는다.

### 수동 확인 (Windows PC)
- 유튜브 캡처 그림 여러 장과 스캔 PDF를 넣어 결과를 원본과 비교한다.
