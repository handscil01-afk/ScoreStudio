# 외부 구성 요소

개발 규칙 9에 따라 앱과 함께 배포하는 외부 구성 요소를 기록한다.

## Bravura (음악 기호 글꼴)
| 항목 | 내용 |
|---|---|
| 용도 | 음자리표, 박자표, 중괄호 등 악보 기호 표시 (SMuFL 표준 글꼴) |
| 버전 | 1.392 (수정하지 않은 원본) |
| 제작 | Steinberg Media Technologies GmbH |
| 라이선스 | SIL Open Font License 1.1, 예약 글꼴 이름 "Bravura" → `ScoreStudio/Fonts/Bravura-OFL.txt` |
| 출처 | npm 패키지 `@vexflow-fonts/bravura` 1.0.2 (VexFlow 프로젝트가 https://github.com/steinbergmedia/bravura 의 `redist`를 그대로 묶은 패키지) |
| SHA-256 | `dca2d90c88437a701b1c2e71fa54e76f9fa41d7deee935d74dc871ea66ecfdd2` (`Bravura.otf`) |
| 유지보수 | Steinberg가 SMuFL 참조 글꼴로 계속 관리한다. |
| Windows 배포 조건 | 글꼴 파일을 수정하지 않고, 라이선스 문서를 함께 배포한다. 글꼴만 따로 판매하지 않는다. 수정하면 "Bravura"라는 이름을 쓸 수 없다. 앱은 글꼴을 시스템에 설치하지 않고 실행 폴더의 `Fonts/`에서 읽는다. |

글꼴을 교체하거나 버전을 올리면 이 표와 SHA-256 값을 함께 고친다.
