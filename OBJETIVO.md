# Objetivo deste repositório

> Repositório `when.it` — abriga o produto **DbSense**.
> O nome do repositório é o nome do produto/iniciativa ("when it… → reaja"); o
> código, a solution (`DbSense.sln`) e a documentação usam o nome DbSense.

## Por que este repositório existe

Sistemas legados críticos (ERPs, sistemas contábeis, aplicações de linha de
negócio) raramente podem ser alterados para publicar eventos de domínio: não há
código-fonte acessível, o risco de mexer é alto, ou o fornecedor não permite.
Ao mesmo tempo, o negócio precisa reagir ao que acontece nesses sistemas —
disparar integrações, notificar, alimentar filas, acionar processos.

Este repositório existe para resolver exatamente isso: **transformar operações
DML de um SQL Server em eventos de negócio e reações, sem tocar em uma linha da
aplicação alvo.**

## A proposta de valor

Configuração **por demonstração**, não por especificação técnica:

1. O analista **grava** — executa a ação real no sistema legado (ex.: "lançar
   uma nota fiscal").
2. A plataforma **captura** o SQL emitido, via Extended Events do SQL Server, e
   extrai a estrutura das operações.
3. A plataforma **infere uma regra** — trigger + companions — por heurística
   (ScriptDom) e, opcionalmente, com apoio de LLM.
4. O analista **revisa e ativa** a regra.
5. Em produção, o motor faz o **matching** dos eventos SQL contra as regras
   ativas (correlação por janela temporal ou por transação, com idempotência) e
   dispara a **reação**: um processo (`cmd`), um comando `sql` ou uma mensagem
   `rabbit`, com entrega garantida por outbox transacional e retry.

O ganho é o tempo de configuração e o não-risco: quem conhece o negócio
configura a integração operando o sistema, e a aplicação alvo permanece
intocada.

## O que este repositório entrega

| Parte | Papel |
|---|---|
| `src/DbSense.Api` | API REST: auth, setup, conexões, recordings, regras, dashboard |
| `src/DbSense.Worker` | Workers: coleta XE, matching, execução de reações, comandos |
| `src/DbSense.Core` | Domínio, EF, parser SQL, inferência, engine, reações, segurança |
| `src/DbSense.Contracts` | DTOs compartilhados entre API, Worker e frontend |
| `frontend/` | UI principal (Vite + React + TS + Tailwind) |
| `electron/` | Shell desktop — empacota API + Worker + UI num único executável |
| `sandbox/` | App de contabilidade real, usado como alvo para validar o pipeline |
| `docs/`, `spec-dbsense-mvp.md` | Arquitetura, reações, operação e a spec original |

## O que este repositório NÃO se propõe a ser

- **Não é um CDC genérico.** O escopo é SQL Server via Extended Events; Postgres,
  MySQL, Oracle e Debezium estão fora do MVP.
- **Não é um ESB nem um orquestrador de workflow.** Ele detecta e dispara; a
  lógica de negócio da reação vive do outro lado.
- **Não substitui instrumentação da aplicação.** Onde é possível alterar o
  código-fonte para publicar eventos, isso continua sendo melhor. O DbSense
  existe para o caso em que isso não é possível.
- **Não observa o que o parser não enxerga** — corpo de stored procedures, por
  exemplo (ver "Limitações conhecidas" no [README](README.md)).

## Critério de sucesso

Um analista que não escreve código consegue, a partir de uma ação executada no
sistema legado, ter uma integração ativa e idempotente em produção — sem
deploy da aplicação alvo e sem escrever a regra à mão.

## Por onde continuar

- [README.md](README.md) — o que está implementado e como rodar
- [docs/architecture.md](docs/architecture.md) — pipeline interno
- [docs/reactions.md](docs/reactions.md) — tipos de reação e placeholders
- [docs/production.md](docs/production.md) — operação e segurança
- [spec-dbsense-mvp.md](spec-dbsense-mvp.md) — especificação de referência
