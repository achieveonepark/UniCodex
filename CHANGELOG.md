# Changelog

## [1.0.0] - 2026-03-01

### Added

- 초기 릴리즈: `com.achieve.uni-codex` 패키지 공개
- `Tools/Codex/Codex Chat` 에디터 채팅 창 추가
- Codex 설치/로그인 상태 점검 및 Device Auth 로그인/로그아웃 흐름 추가
- `Plan`/`Build` 채팅 모드 및 Build 전용 `Diff On` 토글 추가
- `Codex Diff Preview` 창 추가
- 파일별 탭 렌더링, 라인 통계(+/-), 패치 적용(`Apply`) 지원
- 현재 Diff 재정제(`Refine`) 요청 지원
- 프로젝트 외부 경로/위험 경로 차단을 포함한 안전한 패치 적용 로직 추가
- 다중 세션 생성/전환 및 세션별 Codex thread 연동 추가
- 채팅/세션 이력 영속화(`Library/CodexChatHistory.json`) 추가
- `@mention` 파일 자동완성과 타겟 파일 컨텍스트 첨부 기능 추가
- 세션 토큰 사용량 추적 및 원형 토큰 게이지 UI 추가
- Unity Action Bridge 추가 (`Library/CodexUnityActions.json`)
- 지원 액션: `AddComponent`, `RemoveComponent`, `CreateSpriteObject`
- Unity Helper 메뉴 추가: `Tools/Codex/Unity Helper/Apply Pending Actions`
- Unity Helper 메뉴 추가: `Tools/Codex/Unity Helper/Write Action Template`
- Unity 메인 툴바 플레이 영역 단축 버튼 자동 주입 및 재설치 메뉴 추가
- Codex CLI 래퍼 서비스 추가(경로 탐색/버전 확인, `codex exec --json` 실행, thread resume)
- Codex CLI 출력에서 진행 상태(progress) 메시지 파싱 추가
- Codex CLI 출력에서 토큰 사용량(input/output/total) 추출 추가
