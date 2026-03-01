# UniCodex

Unity Editor에서 Codex CLI를 채팅 기반으로 연결해 코드 작업과 Diff 적용을 지원하는 에디터 패키지입니다.

## 핵심 기능

- `Tools/Codex/Codex Chat` 에디터 창 제공
- Codex 설치/로그인 상태 확인, Device Auth 로그인, 로그아웃
- `Plan` / `Build` 모드 전환
- Build 모드 `Diff On` 시 변경안을 Unified Diff로 미리보기 후 적용
- Diff 미리보기 창에서 파일별 탭, 라인별 색상 렌더링, `Apply` / `Refine` 지원
- 다중 채팅 세션(세션 생성, 전환, 이력 보존)
- `@파일경로` 멘션 기반 타겟 파일 컨텍스트 첨부(`Assets/`, `Packages/`)
- 세션 토큰 사용량 표시(원형 게이지 + in/out/total 요약)
- Unity Action Bridge(JSON 파일 기반): 씬/오브젝트 작업 자동 적용
- Unity 메인 툴바(Play 버튼 영역) 단축 버튼 자동 주입

## 요구 사항

- Unity `2022.3` 이상
- Codex CLI 설치
- Codex 계정 로그인 상태

## 설치

### Unity Package Manager (Git URL)

1. Unity에서 `Window > Package Manager` 열기
2. 좌상단 `+` 버튼 클릭
3. `Add package from git URL...` 선택
4. 아래 URL 입력

```text
https://github.com/achieveonepark/unicodex.git#1.0.0
```

### `manifest.json`에 직접 추가

`Packages/manifest.json`의 `dependencies`에 추가:

```json
"com.achieve.uni-codex": "https://github.com/achieveonepark/unicodex.git#1.0.0"
```

## 빠른 시작

1. `Tools/Codex/Codex Chat` 실행 (또는 툴바 단축 버튼 클릭)
2. 우상단 `Settings`에서 `Login (Device)` 진행 후 `Refresh`
3. `Plan` 또는 `Build` 모드 선택
4. 프롬프트 입력 후 `Send` (`Enter` 전송, `Shift+Enter` 줄바꿈)
5. 특정 파일만 컨텍스트에 넣고 싶으면 `@Assets/...` 또는 `@Packages/...` 멘션 사용
6. Build에서 `Diff On` 활성화 시 `Codex Diff Preview` 창에서 검토 후 적용

## 모드 동작

- `Plan`: 분석/설계 중심 응답 유도
- `Build`: 구현 중심 응답 유도
- `Diff On` (Build 전용): 실제 파일 쓰기 대신 Unified Diff 생성을 요청하며, 응답은 Diff Preview 창에서 파일 단위 탭으로 분리 표시

## Diff Preview

- `Apply`: 활성 탭의 패치를 프로젝트 파일에 적용
- `Refine`: 현재 Diff + 추가 요구사항으로 재정제 요청
- 경로 안전성 검사: 프로젝트 외부 경로, `.`/`..` 세그먼트 포함 경로 차단
- `NO_CHANGES` 응답 처리 지원