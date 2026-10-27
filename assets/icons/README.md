# Oranda App Icons

이 디렉토리는 **Oranda (Oracle 개발·운영 도구)**의 공식 앱 아이콘 시안 리소스들을 담고 있습니다.

## 📁 파일 구성
각 시안별로 원본 JPG(1024x1024), 무손실 PNG(1024x1024), Windows용 다중 해상도 ICO(16~256), macOS용 ICNS(16~1024)가 포함되어 있습니다.

| 번호 | 디자인 명칭 | ICO (Windows) | PNG (1024) |
|---|---|---|---|
| **O (공식 채택)** | **Amber Glass 'O' Borderless (오렌지 3D 'O' 레터마크)** | `oranda-o-amber-glass-borderless.ico` | `oranda-o-amber-glass-borderless.png` |
| **O-vortex** | Tech Vortex 'O' (네온 블루 코어) | `oranda-o-tech-vortex.ico` | `oranda-o-tech-vortex.png` |
| **v7** | Goldfish Only Borderless (보더리스 금붕어 단독) | `oranda-v7-goldfish-borderless.ico` | `oranda-v7-goldfish-borderless.png` |
| **v5** | Modern DB Cylinder (모던 DB 실린더) | `oranda-v5-modern-cylinder.ico` | `oranda-v5-modern-cylinder.png` |
| **v6** | Streamlined Goldfish + DB | `oranda-v6-streamlined-goldfish.ico` | `oranda-v6-streamlined-goldfish.png` |
| **v3** | 3D Claymorphism Mascot (공식 마스코트, `assets/mascot/` 이동) | `oranda-v3-claymorphism-mascot.ico` | `oranda-v3-claymorphism-mascot.png` |
| **v1** | macOS Modern Squircle | `oranda-v1-macos-squircle.ico` | `oranda-v1-macos-squircle.png` |
| **v2** | Minimalist Geometric Vector | `oranda-v2-minimalist-vector.ico` | `oranda-v2-minimalist-vector.png` |
| **v4** | Futuristic Cyber Hologram | `oranda-v4-cyber-hologram.ico` | `oranda-v4-cyber-hologram.png` |

## 🛠️ 앱 적용 방법 (Avalonia)
원하는 시안을 `src/Oranda.App/Assets/` 디렉토리로 복사한 뒤, `MainWindow.axaml` 및 빌드 타깃에 지정할 수 있습니다.
```xml
<Window ...
        Icon="/Assets/oranda.ico">
```
