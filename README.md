# Capture Tool (V0.1)

고정 크기 화면 캡처 도구 (Windows) — Python 등 별도 설치 없이 동작

## 빌드 / 실행
1. `build.bat` 더블클릭 → Windows 내장 .NET Framework 4.x 컴파일러로 `CaptureTool_V0.1.exe` 생성 후 자동 실행
2. 이후에는 `CaptureTool_V0.1.exe` 하나만 복사해서 사용

## 기능
- **크기 지정**: 프리셋 선택 / W·H 숫자 입력(Enter) / 바깥 회색 띠 드래그 (좌·우·아래 변 및 모서리)
- **창**: 항상 위에 표시, 최소화·최대화·종료 버튼만 제공, 상단 바 또는 회색 띠 위쪽 드래그로 이동
- **캡처**: 버튼 / 전역 단축키 **F9** / 창 활성화 상태(상단 바 클릭)에서 **Ctrl+C**
  - 지정 폴더에 `Capture_날짜_시간_WxH.png` 저장
  - 클립보드 복사 → Ctrl+V로 바로 붙여넣기
- **설정 기억**: 크기·위치·폴더 → `%APPDATA%\CaptureTool\settings.ini`
- DPI 배율(125%, 150% 등)과 무관하게 실제 픽셀 기준으로 캡처

## Python 버전 (참고용)
`python/capture_tool_V0.1.py` — 동일 기능의 tkinter 버전 (Python + Pillow 필요)
```
pip install pillow
python python/capture_tool_V0.1.py
```

## 버전 관리
- 버전은 파일명에 표기 (`CaptureTool_V0.1.cs`, `capture_tool_V0.1.py`)
- 새 버전 시 파일명과 `build.bat`의 `VER` 값을 함께 변경
