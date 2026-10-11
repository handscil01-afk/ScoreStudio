# Sprint 3 — MusicXML 가져오기 설계와 수용 기준

## 라이브러리 선택
외부 라이브러리를 쓰지 않고 .NET 기본 구성 요소인 `System.Xml.Linq`와 `System.IO.Compression`으로 직접 구현한다.

| 후보 | 상태(2026-10 확인) | 판단 |
|---|---|---|
| MusicXml.Net 3.1.0 | BSD-3-Clause, 2024-08 갱신 | 사용하지 않음. 객체 모델이 모르는 요소를 버리므로 "지원하지 않는 기호"를 빠짐없이 경고하기 어렵다. |
| Manufaktura.Controls 1.4.2 | MIT, 2019-01 이후 갱신 없음 | 사용하지 않음. 유지보수가 멈췄다. |
| 직접 구현 (System.Xml.Linq) | .NET에 포함, 추가 라이선스 없음 | **채택.** 필요한 범위가 작고, 모든 요소를 직접 보며 경고를 만들 수 있다. |

## 지원 범위
### 가져오는 것
| MusicXML | ScoreStudio |
|---|---|
| `score-partwise` (`.musicxml`, `.xml`) | 프로젝트 |
| 압축 MusicXML (`.mxl`, `META-INF/container.xml`) | 프로젝트 |
| `work-title`, 없으면 `movement-title`, 없으면 파일 이름 | 제목 |
| `creator type="composer"` | 작곡자 |
| `score-part` / `part-name` / `instrument-name` | 파트 이름 / 악기 |
| `part-name print-object="no"`, `part-group` (`group-symbol`, `group-barline`) | 이름 표시 여부, 파트 묶음(괄호 종류, 마디선 연결) — 스키마 7 |
| `defaults` (`scaling`, `page-layout`, `system-layout`, `staff-layout`), `print`의 `system-layout`·`staff-layout` | 보표 크기, 용지(A4·Letter), 여백, 기본·줄별 줄 간격과 보표 간격 — 스키마 8 |
| `lyric`의 `default-y`·`placement`, `defaults/lyric-font` | 가사 줄 높이, 가사 글꼴 크기 — 스키마 9 |
| 글자 없는 `lyric`의 `extend type="stop"` | 연장선이 끝나는 음표(절 번호) — 스키마 11 |
| `harmony`의 `default-y`, `degree print-object="no"` | 코드 이름 높이 — 스키마 10. 표시하지 않는 구성음은 이름에 붙이지 않는다(예: Finale의 "C2/E"). |
| `measure number` | 마디 번호 (숫자가 아니면 순서 번호를 쓰고 경고) |
| `attributes`: `divisions`, `time`, `key/fifths`, `staves`, `clef` | 길이 단위, 박자표, 조표(5도권 수), 보표 수, 음자리표 |
| `note` + `pitch` (`step`, `alter`, `octave`) | 음표: MIDI 음높이, 철자(예: `Eb5`) |
| `note` + `rest` (`measure="yes"` 포함) | 쉼표 |
| `chord` | 같은 위치의 음표(화음) |
| `duration`, `backup`, `forward` | 길이·위치(4분음표 단위 분수, 잇단음표 포함) |
| `staff`, `voice` | 보표 번호, 성부 번호 |
| 모든 `lyric` (`number`별 절, `syllabic`, `extend`, 여러 `text`는 ‿로 연결) | 가사 (절, 음절 위치, 연장선) — 스키마 4 |
| `tie type="start"`, `tied type="start"` | 다음 음으로 이어지는 붙임줄 — 스키마 4 |
| `harmony` (`root`, `kind`와 `kind@text`, `bass`, `degree`, `offset`, `staff`) | 코드 이름 (구조화된 값과 표시 문자열, 예: `Bbmaj7/D`) — 스키마 4 |
| `barline/repeat` (`forward`, `backward`, `times`) | 도돌이표 시작·끝, 반복 횟수 — 스키마 4 |
| `barline/ending` (`start`, `stop`, `discontinue`) | 볼타: 번호, 표시 글자, 마디 수, 열린 끝 — 스키마 4 |
| 마디 중간·마디 시작의 `clef` 변경 | 음자리표 변경: 보표, 마디 안 위치, 음자리표 — 스키마 5 |
| `direction/octave-shift` (`down`=8va, `up`=8vb, `size` 8·15, `stop`) | 음표마다 옥타브 기호 값(1=8va, 2=15ma, −1=8vb, −2=15mb). 음높이는 실제 소리 그대로 — 스키마 5 |
| `staff-details print-object="no"/"yes"` | 마디마다 숨긴 보표 목록(Finale의 "빈 보표 숨기기") — 스키마 5 |
| `print new-system="yes"`, `new-page="yes"` | 원본의 줄 나눔과 페이지 나눔 — 스키마 5 |
| `beam` (`number` 1~6, `begin`/`continue`/`end`/`forward hook`/`backward hook`) | 음표마다 단계별 빔 정보 — 스키마 6 |

### 경고를 내고 가져오지 않는 것
꾸밈음, 큐 음표, 타악기 음표(`unpitched`), 이음줄, 잇단음표 괄호 표시, 아티큘레이션, 장식음, 페르마타, 셈여림, 빠르기·글자 지시, 크레셴도, 페달, 세뇨·코다, 로마 숫자 화성 기호, 코드 다이어그램, 숫자 저음, 울림 붙임줄(let ring), 미분음, 22ma 이상의 옥타브 기호(15ma로 가져옴), 지원하지 않는 음자리표(비슷한 음자리표로 바꿈).
- 경고는 종류별로 묶고 개수와 처음 나온 위치(파트·마디)를 보여 준다.

### 경고 없이 무시하는 것
화면 표시용 정보로, 나중에 ScoreStudio가 직접 다시 계산한다: `type`, `dot`, `stem`, `accidental`(표시용), `notehead`, `print`의 줄·페이지 나눔 외 속성, `defaults`, `credit`, 좌표·글꼴 속성, `sound`, `midi-instrument`.

### 지원하지 않는 파일
`score-timewise`, MusicXML이 아닌 XML, 손상된 압축 파일은 한국어 오류로 거절한다.

## 데이터 모델 변경 (스키마 버전 2 → 3)
| 대상 | 추가 항목 | 설명 |
|---|---|---|
| `ScoreMeasure` | `KeyFifths` (기본 0) | 조표. 5도권 수 −7~7 (예: −3은 내림마장조/다단조). 아직 화면에 그리지 않는다. |
| `ScoreEvent` | `Staff` (기본 0) | 파트 안의 보표 번호(0부터). 파트의 보표 수보다 작아야 한다. |
| `ScoreEvent` | `Voice` (기본 1) | 성부 번호(1 이상). |

- 버전 1·2 파일은 그대로 열리고 새 항목은 기본값으로 채워진다. 저장은 버전 3으로 한다.
- 위치는 마디 시작부터의 4분음표 단위 분수, 길이도 4분음표 단위 분수다(기존과 같음). 분수는 약분해서 저장한다.

## 안전한 파일 처리
- 원본 파일은 읽기 전용으로 열고 절대 쓰지 않는다. 가져온 결과는 사용자가 "저장"을 눌러 `.scoreproj`로 따로 저장한다.
- XML의 DTD는 처리하지 않고 외부 파일·주소도 읽지 않는다(MusicXML 파일 머리의 DOCTYPE은 그대로 허용).
- 파일 크기 제한: 원본 50MB, `.mxl` 안의 악보 XML 200MB.

## 화면
- "파일 → MusicXML 가져오기..."와 도구 막대 "가져오기" 버튼을 켠다.
- 가져오기가 끝나면 결과 창에 요약(파트·마디·음표·쉼표·가사 수)과 경고 목록을 보여 준다.
- 음표는 가져와서 저장하지만 아직 화면에 그리지 않는다(Sprint 4·5). 결과 창과 상태 표시줄에 이를 분명히 적는다.

## 수용 기준
### 자동 테스트 (`ScoreStudio.Core.Tests`)
1. 단선율 샘플: 제목, 작곡자, 파트 이름, 박자표, 조표, 음높이, 철자, 길이, 위치, 쉼표, 가사가 원본과 같다.
2. 피아노 샘플: 보표 2개(높은음·낮은음자리표), `backup`으로 나뉜 두 보표의 음표 위치, 화음, 보표·성부 번호가 원본과 같다.
3. 잇단음표와 `divisions` 변경: 셋잇단 8분음표의 길이가 1/3이다.
4. 지원하지 않는 요소: 종류별 경고와 개수가 맞고, 꾸밈음은 가져오지 않는다.
5. 원본과 결과 비교: 원본에서 직접 센 마디 수·음표 수·쉼표 수·가사 수가 가져온 결과와 같다(모든 샘플).
6. `.mxl`: `container.xml`이 있는 경우와 없는 경우 모두 가져온다.
7. 잘못된 파일(빈 파일, XML 아님, timewise, 파트 목록과 맞지 않는 파트, 손상된 zip)은 한국어 오류를 낸다.
8. 외부 엔터티를 포함한 XML도 외부 파일을 읽지 않는다.
9. 가져오기 전후로 원본 파일의 내용과 수정 시각이 같다.
10. 가져온 프로젝트를 저장했다가 다시 열면 모든 값이 같다.
11. 스키마 버전 1·2 파일이 열리고, 잘못된 보표 번호·성부 번호·조표는 손상 파일로 거절한다.

### 수동 테스트 (Windows PC)
1. `samples/musicxml/`의 각 파일을 가져오면 결과 창에 요약과 경고가 보이고, 악보 화면에 파트·보표·박자표·마디가 나타난다.
2. 가져온 뒤 저장하고 다시 열어도 같은 화면이 나온다.
3. 사용자가 가진 다른 프로그램(예: Finale, MuseScore)에서 내보낸 MusicXML 파일로도 시험하고, 결과를 이 문서에 기록한다.

## 실제 파일 시험 결과 (2026-10-10, 사용자가 Finale에서 내보낸 파일)
원본 파일은 저작권이 있을 수 있어서 저장소에 넣지 않는다. 결과만 기록한다.

| 파일 | 파트 | 마디 | 음표 | 쉼표 | 가사 | 경고(개수) |
|---|---|---|---|---|---|---|
| 하나님의 새계-G-중주.mxl | 2 | 57 | 446 | 61 | 184 | 코드 이름 96, 가사 연장선 76, 두 번째 이후 가사 절 67, 붙임줄 41, 볼타 8, 도돌이표 4, 하이픈 1 |
| 다윗의 노래(총보)-F-수정.mxl | 4 | 117 | 3532 | 293 | 463 | 아티큘레이션 596, 붙임줄 334, 셈여림 61, 이음줄 46, 크레셴도 37, 잇단음표 표시 29, 글자 지시 27, 글리산도 24, 하이픈 22, 옥타브 기호 10, 페르마타 8, 점선 지시 4, 메트로놈 3, 음자리표 변경 2, 장식음 2 |
| 소원.mxl | 1 | 33 | 249 | 25 | 179 | 붙임줄 70, 하이픈 69, 코드 이름 59, 볼타 4, 도돌이표 2 |

- 세 파일 모두 오류 없이 가져왔다.
- 발견한 문제: Finale은 이름 없는 보표를 "MusicXML Part"로 내보내므로 모든 파트 이름과 경고 위치가 같게 보였다. → 인쇄용 이름(`part-name-display`)을 우선 쓰고, 임시 이름은 "파트 N"으로 바꾸며, 이름이 겹치면 경고 위치에 "N번째 파트"를 붙이도록 고쳤다.
- `<dashes>`(cresc. - - -)에 한국어 경고 이름을 붙였다.
- 이 악보들에서 많이 나온 요소는 붙임줄, 코드 이름, 두 번째 이후 가사 절, 도돌이표·볼타다. 다음 단계에서 데이터 모델에 넣을 우선순위가 높다.

## 데이터 보존 확장 (스키마 버전 3 → 4)
실제 Finale 파일에서 많이 나온 요소를 저장되도록 데이터 모델에 넣었다. 화면에는 아직 그리지 않는다.

| 대상 | 추가 항목 | 설명 |
|---|---|---|
| `ScoreEvent` | `Lyrics: List<ScoreLyric>` | 절마다 하나: `Verse`(1부터), `Text`, `Syllabic`(Single/Begin/Middle/End, Begin·Middle 뒤에 하이픈), `Extend`(연장선) |
| `ScoreEvent` | `TieToNext` | 다음 같은 음으로 이어지는 붙임줄 |
| `ScoreMeasure` | `RepeatStart`, `RepeatEnd`, `RepeatTimes` | 도돌이표 시작·끝, 전체 연주 횟수(기본 2) |
| `ScoreMeasure` | `Volta: ScoreVolta?` | 볼타가 시작하는 마디에만 둔다: `Numbers`("1", "1, 2"), `Text`("1."), `MeasureCount`, `OpenEnd` |
| `ScoreMeasure` | `ChordSymbols: List<ScoreChordSymbol>` | 위치, 보표, `RootStep`/`RootAlter`, `Kind`(MusicXML 이름), `BassStep`/`BassAlter`, 표시 문자열 `Text` |

- 버전 1~3 파일의 `Lyric`은 1절 가사로 옮기고, 저장할 때는 더 이상 쓰지 않는다.
- 결과 창 요약에 가사 절 수, 붙임줄 수, 코드 이름 수를 추가했다.
- 새 샘플 `samples/musicxml/hymn-repeats-chords.musicxml`: 2절 가사, 하이픈, 연장선, 붙임줄, 코드 이름 8개(분수 코드, sus4, N.C., add9, `kind@text`), 도돌이표, 1·2번 볼타. 경고 없이 모두 가져와야 한다.

### 스키마 4 적용 후 다시 시험한 결과 (2026-10-10)
| 파일 | 가사(절) | 붙임줄 | 코드 이름 | 남은 경고 |
|---|---|---|---|---|
| 하나님의 새계-G-중주.mxl | 184 (2절) | 41 | 96 | 없음 |
| 다윗의 노래(총보)-F-수정.mxl | 463 (2절) | 334 | 0 | 셈여림, 아티큘레이션, 이음줄, 크레셴도, 잇단음표 표시, 글자 지시, 글리산도, 옥타브 기호, 페르마타, 점선 지시, 메트로놈, 장식음, 음자리표 변경 |
| 소원.mxl | 179 (1절) | 70 | 59 | 없음 |

- 경고 위치가 "파트 1 27마디", "파트 4 7마디"처럼 파트를 구분해 나온다.
- 작곡자 칸이 여러 줄(예: "작사 작곡" / "조옮김")인 파일이 있어서, 레이아웃에서 한 줄씩 배치하도록 고쳤다.
