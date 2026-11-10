# Capture Tool

고정 크기 화면 캡처 도구 (Windows) — Python 등 별도 설치 없이 동작

## 빌드 / 실행
1. `build.bat` 더블클릭 → Windows 내장 .NET Framework 4.x 컴파일러로 `CaptureTool.exe` 생성 후 자동 실행
2. 이후에는 `CaptureTool.exe` 하나만 복사해서 사용

## 기능
- **크기 지정**: 프리셋 선택 / W·H 숫자 입력(Enter) / 우하단 ◢ 드래그
- **창**: 항상 위에 표시, 최소화·최대화·종료 버튼만 제공, 상단 바 드래그로 이동
- **캡처**: 버튼 또는 전역 단축키 **F9**
  - 지정 폴더에 `Capture_날짜_시간_WxH.png` 저장
  - 클립보드 복사 → Ctrl+V로 바로 붙여넣기
- **설정 기억**: 크기·위치·폴더 → `%APPDATA%\CaptureTool\settings.ini`
- DPI 배율(125%, 150% 등)과 무관하게 실제 픽셀 기준으로 캡처
