# ScoreStudio — Finale 대체 사보 프로그램 (초기 개발 킷)

Windows용 사보 프로그램의 첫 번째 개발 기반입니다. 목표는 `.musx` 원본을 보존하면서 MusicXML로 가져오고, 자체 프로젝트 형식으로 저장하며, 이후 편집 기능을 확장하는 것입니다.

## 현재 포함된 것
- .NET 8 / WPF 프로젝트 뼈대
- 악보 작업 화면의 기본 레이아웃
- 프로젝트 모델 초안
- `CLAUDE.md` 개발 지침
- 단계별 로드맵 및 수동 테스트 기준

## 현재 포함되지 않은 것
- 완성된 악보 렌더러
- 실제 `.musx` 변환기 연결
- MusicXML 파서/내보내기
- PDF 출력
- 완성된 채보 편집 기능

이 킷은 실행 가능한 출발점이지 완성된 사보 프로그램이 아닙니다. 먼저 빌드한 뒤 기능을 작은 단위로 구현하세요.

## 프로젝트 구조
| 폴더 | 내용 |
|---|---|
| `ScoreStudio/` | WPF 앱(화면). Windows에서만 빌드된다. |
| `ScoreStudio.Core/` | 악보 데이터 모델과 `.scoreproj` 저장/열기. 화면 코드와 분리되어 있다. |
| `ScoreStudio.Core.Tests/` | 저장/재열기, 잘못된 파일 처리 자동 테스트(xUnit). |
| `samples/` | 테스트용 샘플 프로젝트 파일. |
| `.github/workflows/build.yml` | GitHub Actions의 Windows 실행기에서 빌드·테스트하고 실행 파일을 올린다. |

## 실행
필요: Windows 10/11, .NET 8 SDK, Visual Studio 2022 또는 `dotnet` CLI.

```powershell
dotnet restore ScoreStudio.sln
dotnet build ScoreStudio.sln -warnaserror
dotnet test ScoreStudio.sln
dotnet run --project ScoreStudio
```

Windows가 아닌 환경에서는 WPF 앱을 빌드할 수 없다. 이때는 `dotnet test ScoreStudio.Core.Tests`로 저장/열기 테스트만 실행한다.

### 빌드된 앱 내려받기
`main`에 push할 때마다 CI가 "latest" 릴리스의 zip 파일을 새로 올린다. GitHub에 로그인한 브라우저에서 아래 주소를 열면 바로 내려받을 수 있다.

https://github.com/handscil01-afk/ScoreStudio/releases/download/latest/ScoreStudio-win-x64.zip

압축을 풀고 `ScoreStudio.exe`를 실행한다. 이 파일을 실행하려면 PC에 [.NET 8 Desktop Runtime](https://dotnet.microsoft.com/download/dotnet/8.0)이 설치되어 있어야 한다.

## 첫 개발 목표
1. 앱이 실행되고 기본 작업 화면이 표시되는지 확인
2. 자체 프로젝트 JSON 저장/열기 구현
3. 악보 데이터 모델과 테스트 추가
4. MusicXML 가져오기
5. 실제 `.musx` 파일을 별도 테스트해 변환 정확도 평가

## 안전한 파일 처리
- 원본 `.musx` 파일을 절대 수정하거나 덮어쓰지 않습니다.
- 가져온 파일은 임시 작업 폴더에서 처리합니다.
- 변환 실패 시 원본 파일과 오류 로그를 보존합니다.
- 변환 경고는 사용자에게 숨기지 않습니다.
