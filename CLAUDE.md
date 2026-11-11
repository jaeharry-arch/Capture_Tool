# Capture Tool — 작업 이어가기 가이드

허재님이 "Capture_Tool V0.x 폴더로 업데이트" 처럼 **버전 폴더만 지정**하면,
이 문서를 기준으로 해당 폴더의 파일을 가져와 다음 버전 작업을 이어간다.

## 저장소 구조
```
CLAUDE.md          ← 이 문서 (작업 규칙 / 구현 메모 / 미해결 사항)
CHANGELOG.md       ← 버전별 변경 내역
README.md          ← 저장소 개요, 최신 버전 안내
new_version.bat    ← 새 버전 폴더 생성 (new_version.ps1 호출)
V0.1/              ← 버전별 독립 설치 세트 (이 폴더만 복사하면 설치 가능)
  CaptureTool_V0.1.cs
  build.bat        (VER=V0.1 → CaptureTool_V0.1.exe)
  README.md        (설치 방법)
  python/capture_tool_V0.1.py   (선택, 동일 기능 Python 버전)
```

## 버전 규칙
- 버전은 **폴더명 + 파일명 + build.bat의 VER + 창 제목**에 모두 같은 값(`V0.x`)으로 표기
- 이전 버전 폴더는 **수정하지 않음** (보관용). 작업은 항상 새 버전 폴더에서
- 최신 버전 = 번호가 가장 큰 `V*` 폴더
- 새 버전 번호는 매번 허재님께 먼저 확인
- Drive 백업: `Capture` 폴더(https://drive.google.com/drive/folders/1wbSAg4U_jNnQ6wLxJexbzTQf3GN5IRg6)
  아래 같은 버전 하위 폴더(`V0.x`)에 버전 폴더 내용을 세트로 업로드 — 업로드 전 버전 확인
- git 태그는 사용하지 않음 (Claude 세션에서 태그 push 불가 → 파일명 버전 표기로 대체)

## 업데이트 절차
1. 기준 폴더 확인 (지정 없으면 최신 버전 폴더)
2. 새 버전 폴더 생성
   - Windows: `new_version.bat V0.1 V0.2`
   - Claude(Linux): 폴더 복사 → 파일명/내용의 `V0.1` → `V0.2` 치환 (`.cs`는 UTF-8 BOM 유지, `.bat`은 CRLF·BOM 없음 유지)
3. 새 폴더의 파일만 수정 — C#과 Python 버전 기능을 함께 맞춤
4. `CHANGELOG.md`에 항목 추가, 루트 `README.md`의 최신 버전 갱신
5. 커밋/푸시 → Drive `Capture/V0.x` 백업

## 구현 메모 (C# — `CaptureTool_V0.x.cs`)
- 컴파일러: Windows 내장 .NET Framework 4.x `csc.exe` → **C# 5 문법만** 사용
  (`$"..."`, `?.`, `nameof`, `=>` 멤버, `out var` 불가)
- 소스는 UTF-8 **BOM 포함**, build.bat에서 `/codepage:65001`
- DPI: Per-Monitor V2 → Per-Monitor → System 순으로 시도 (Main 시작 시)
- 창: `FormBorderStyle.None`, `TopMost`, `TransparencyKey = Magenta` (투명 영역은 클릭 통과)
- 레이아웃(위→아래): 툴바 → 회색 띠(`GB = S(8)`) → 빨간 테두리(`B = 3`) → 투명 여유(`GAP = 1`) → 캡처 영역(`regionRect`)
- 크기 조절: 회색 띠 좌/우/하단·모서리 드래그 (`HitZone`), 위쪽 띠는 창 이동
- 캡처: `CopyFromScreen(PointToScreen(regionRect))` → PNG 저장 + `CopyToClipboard` (Win32 CF_DIB + "PNG")
- 단축키: F9 = `RegisterHotKey` 전역 / Ctrl+C = `ProcessCmdKey` → `BeginInvoke(DoCapture)`
- 설정: `%APPDATA%\CaptureTool\settings.ini` (folder, w, h, x, y)
- UI 폰트: 한글 맑은 고딕, 숫자 Arial, 창 버튼 Segoe MDL2 Assets

## 미해결 / 확인 대기
- Claude 작업 환경이 Linux라 **Windows 실기 빌드·동작은 미검증** — 허재님 확인 결과에 따라 수정
- V0.1에서 확인 대기: Ctrl+C 클립보드 복사, 빨간 테두리 혼입 수정 효과
