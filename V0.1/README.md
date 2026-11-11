# Capture Tool V0.1 — 설치 세트

이 폴더 하나만 있으면 설치됩니다. (Python 등 별도 프로그램 불필요)

## 설치
1. 이 폴더(`V0.1`)를 PC의 원하는 위치에 복사
2. `build.bat` 더블클릭
   → Windows 내장 .NET Framework 4.x 컴파일러로 `CaptureTool_V0.1.exe` 생성 후 자동 실행
3. 이후에는 `CaptureTool_V0.1.exe` 하나만 원하는 곳에 두고 사용

## 파일
| 파일 | 용도 |
|---|---|
| `CaptureTool_V0.1.cs` | 프로그램 소스 (C# WinForms) |
| `build.bat` | exe 빌드 스크립트 (`VER=V0.1`) |
| `python/capture_tool_V0.1.py` | (선택) 동일 기능 Python 버전 — `pip install pillow` 후 실행 |

## 사용법
- **크기 지정**: 프리셋 선택 / W·H 숫자 입력(Enter) / 바깥 회색 띠 드래그 (좌·우·아래 변 및 모서리)
- **이동**: 상단 바 또는 회색 띠 위쪽 드래그
- **캡처**: 캡처 버튼 / **F9** (어디서든) / 상단 바 클릭 후 **Ctrl+C**
  - 지정 폴더에 `Capture_날짜_시간_WxH.png` 저장 + 클립보드 복사 → Ctrl+V
- **설정 기억**: 크기·위치·폴더 → `%APPDATA%\CaptureTool\settings.ini`
