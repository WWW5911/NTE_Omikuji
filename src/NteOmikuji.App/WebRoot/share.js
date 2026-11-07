// Draws share images on a canvas, so a saved file looks the same on any screen.
window.nteShare = (() => {
    const FONT = '"Microsoft JhengHei UI","Microsoft JhengHei","Yu Gothic UI","Segoe UI",sans-serif';
    const THEMES = {
        sakura: {
            bg: "#f9f3f1", surface: "#fefbfa", surface2: "#f7eeec", border: "#eee2df", text: "#4d4240", muted: "#756663",
            accent: "#96606d", soft: "#f7e7e7", bar: "#b4838d", track: "#f1e4e2", petal: "#c7a0a8",
        },
        sumi: {
            bg: "#2c2728", surface: "#362f30", surface2: "#3f3839", border: "#4d4445", text: "#f2e9e6", muted: "#bcaeab",
            accent: "#dcb0b7", soft: "#4d3b3f", bar: "#c79aa3", track: "#4a4142", petal: "#dcb0b7",
        },
    };
    // Japanese traditional colors, matching the app's luck tags, worst tier first.
    const TIERS = [
        { fill: "#433634", ink: "#fff1cf", line: "#6f514c" },
        { fill: "#543f32", ink: "#fff1cf", line: "#6f514c" },
        { fill: "#6a4d32", ink: "#fff1cf", line: "#6f514c" },
        null,
        { fill: "#fff1cf", ink: "#543f32", line: "#f8b400" },
        { fill: "#ffd700", ink: "#543f32", line: null },
        { fill: "#f8b400", ink: "#433634", line: null },
    ];
    const GOLD = "#f8b400";
    const LIMIT_TINT = "#fff1cf";
    const LIMIT_INK = "#543f32";
    const STEPS = ["−2σ", "−1σ", "−0.5σ", "+0.5σ", "+1σ", "+2σ"];
    const STEP_AT = [-2, -1, -0.5, 0.5, 1, 2];
    const SIZES = { overview: [1920, 1080], luck: [1080, 1080] };
    const POOL_WIDTH = 1080, POOL_MIN_HEIGHT = 760, POOL_MAX_HEIGHT = 1350, BAR_ROW = 54;

    let ctx, theme;

    const font = (size, weight = 400) => { ctx.font = `${weight} ${size}px ${FONT}`; };

    function text(value, x, y, size, color, { weight = 400, align = "left", max } = {}) {
        font(size, weight);
        ctx.fillStyle = color;
        ctx.textAlign = align;
        ctx.textBaseline = "alphabetic";
        ctx.fillText(max ? fit(value, max) : value, x, y);
    }

    function fit(value, max) {
        if (ctx.measureText(value).width <= max) return value;
        let cut = value;
        while (cut.length > 1 && ctx.measureText(cut + "…").width > max) cut = cut.slice(0, -1);
        return cut + "…";
    }

    function width(value, size, weight = 400) {
        font(size, weight);
        return ctx.measureText(value).width;
    }

    function box(x, y, w, h, radius, fill, line) {
        ctx.beginPath();
        ctx.roundRect(x, y, w, h, radius);
        if (fill) { ctx.fillStyle = fill; ctx.fill(); }
        if (line) { ctx.strokeStyle = line; ctx.lineWidth = 2; ctx.stroke(); }
    }

    const tierStyle = tier => TIERS[tier] ?? { fill: theme.surface2, ink: theme.text, line: theme.border };

    /** Draws a tag ending at x when right-aligned. Returns its width. */
    function tag(label, tier, x, y, size, { right = false } = {}) {
        const style = tierStyle(tier);
        const pad = size * 0.72;
        const w = width(label, size, 700) + pad * 2;
        const h = size * 1.62;
        const left = right ? x - w : x;
        box(left, y, w, h, h / 2, style.fill, style.line);
        text(label, left + w / 2, y + h / 2 + size * 0.36, size, style.ink, { weight: 700, align: "center" });
        return w;
    }

    function note(label, x, y, size, { right = false } = {}) {
        const pad = size * 0.72;
        const w = width(label, size, 500) + pad * 2;
        const h = size * 1.62;
        box(right ? x - w : x, y, w, h, h / 2, null, theme.border);
        text(label, (right ? x - w : x) + w / 2, y + h / 2 + size * 0.36, size, theme.muted, { weight: 500, align: "center" });
    }

    function poolTag(pool, x, y, size) {
        if (pool.luck) tag(pool.luck.label, pool.luck.tier, x, y, size, { right: true });
        else note(pool.luckNote ?? "—", x, y, size, { right: true });
    }

    /** A five-petal blossom, the one decoration, kept faint behind the content. */
    function blossom(x, y, radius, alpha, turn = 0) {
        ctx.save();
        ctx.translate(x, y);
        ctx.rotate(turn);
        ctx.globalAlpha = alpha;
        ctx.fillStyle = theme.petal;
        for (let i = 0; i < 5; i++) {
            ctx.rotate(Math.PI * 2 / 5);
            ctx.beginPath();
            ctx.moveTo(0, 0);
            ctx.bezierCurveTo(radius * 0.62, -radius * 0.32, radius * 0.6, -radius * 0.92, radius * 0.1, -radius);
            ctx.lineTo(0, -radius * 0.86);
            ctx.lineTo(-radius * 0.1, -radius);
            ctx.bezierCurveTo(-radius * 0.6, -radius * 0.92, -radius * 0.62, -radius * 0.32, 0, 0);
            ctx.fill();
        }
        ctx.restore();
    }

    function background(w, h) {
        ctx.fillStyle = theme.bg;
        ctx.fillRect(0, 0, w, h);
        const faint = theme === THEMES.sumi ? 0.6 : 1;
        blossom(w - 70, 60, 170, 0.14 * faint, 0.3);
        blossom(w - 300, 26, 66, 0.12 * faint, 1.1);
        blossom(44, h - 34, 92, 0.1 * faint, 0.7);
    }

    const day = stamp => stamp?.slice(0, 10).replaceAll("-", "/") ?? "";

    function period(first, last) {
        if (!first) return "";
        return day(first) === day(last) ? day(first) : `${day(first)} – ${day(last)}`;
    }

    const number = value => value.toLocaleString("en-US");
    const signed = value => `${value >= 0 ? "+" : "−"}${Math.abs(value).toFixed(2)}σ`;

    function footer(card, w, h, margin, explain) {
        text(explain, margin, h - 30, 20, theme.muted, { max: w - margin * 2 - 400 });
        text(card.footer, w - margin, h - 30, 20, theme.muted, { align: "right" });
    }

    /** Pulls toward the guarantee, with the point where the odds rise marked. */
    function pityMeter(status, label, x, y, w) {
        text(label, x, y, 22, theme.muted);
        text(`${status.isLowerBound ? "至少 " : ""}${status.current} / ${status.threshold}`, x + w, y, 22, theme.text, { weight: 600, align: "right" });
        const top = y + 12;
        box(x, top, w, 14, 7, theme.track);
        const progress = Math.min(1, status.current / status.threshold);
        if (progress > 0) box(x, top, Math.max(14, w * progress), 14, 7, theme.bar);
        if (status.risesFrom) {
            const at = x + w * status.risesFrom / status.threshold;
            ctx.fillStyle = theme.text;
            ctx.fillRect(at - 1.5, top - 4, 3, 22);
        }
    }

    function drawOverview(card, w, h) {
        const margin = 56;
        background(w, h);
        text("NTE 抽卡紀錄", margin, 92, 56, theme.text, { weight: 700 });
        const facts = [period(card.firstDate, card.lastDate), `${number(card.totalPulls)} 抽`, `S ${card.totalTop} 個`, `限定 ${card.totalLimited} 個`];
        if (card.maskedUid) facts.push(`UID ${card.maskedUid}`);
        text(facts.filter(Boolean).join("・"), margin, 138, 26, theme.muted);

        if (card.overall) {
            const size = 44;
            tag(card.overall.label, card.overall.tier, w - margin, 42, size, { right: true });
            const tagWidth = width(card.overall.label, size, 700) + size * 1.44;
            text(signed(card.overall.sigma), w - margin - tagWidth - 24, 92, 44, theme.text, { weight: 700, align: "right" });
            text(`${card.overall.smallSample ? "樣本少・" : ""}實際 ${card.overall.hits} 個，期望 ${card.overall.expected.toFixed(1)} ± ${card.overall.standardDeviation.toFixed(1)} 個`, w - margin, 138, 24, theme.muted, { align: "right" });
        }

        const scaleY = 164, scaleH = 48, cell = (w - margin * 2) / 7;
        card.tierLabels.forEach((label, tier) => {
            const active = card.overall?.tier === tier;
            const style = active ? tierStyle(tier) : { fill: theme.surface2, ink: theme.muted };
            ctx.beginPath();
            const radii = tier === 0 ? [10, 0, 0, 10] : tier === 6 ? [0, 10, 10, 0] : 0;
            ctx.roundRect(margin + tier * cell + 1, scaleY, cell - 2, scaleH, radii);
            ctx.fillStyle = style.fill;
            ctx.fill();
            text(label, margin + tier * cell + cell / 2, scaleY + 33, 24, style.ink, { weight: active ? 700 : 500, align: "center" });
        });

        const gap = 24, top = 236, bottom = h - 66;
        const column = (w - margin * 2 - gap * 2) / 3;
        card.pools.forEach((pool, index) => poolColumn(pool, margin + index * (column + gap), top, column, bottom - top));
        footer(card, w, h, margin, "比較實際數量與依抽數算出的期望。棋盤看 S，弧盤看限定大獎；機率取自公開資料。");
    }

    function poolColumn(pool, x, y, w, h) {
        const pad = 28, inner = w - pad * 2;
        box(x, y, w, h, 18, theme.surface, theme.border);
        text(pool.label, x + pad, y + 54, 36, theme.text, { weight: 700 });
        poolTag(pool, x + w - pad, y + 22, 24);
        if (pool.luck?.smallSample) {
            text("樣本少", x + w - pad - width(pool.luck.label, 24, 700) - 24 * 1.44 - 12, y + 50, 20, theme.muted, { align: "right" });
        }
        if (pool.totalPulls === 0) {
            text("尚無紀錄", x + w / 2, y + h / 2, 28, theme.muted, { align: "center" });
            return;
        }

        const pullsText = number(pool.totalPulls);
        // A long count shrinks rather than running into the figures beside it.
        let pullsSize = 64;
        while (pullsSize > 36 && width(pullsText, pullsSize, 700) + 42 > inner * 0.42) pullsSize -= 2;
        text(pullsText, x + pad, y + 130, pullsSize, theme.accent, { weight: 700 });
        text("抽", x + pad + width(pullsText, pullsSize, 700) + 8, y + 130, 26, theme.muted);
        const stats = pool.limitedCount === null
            ? [[pool.topLabel, `${pool.topCount}`], ["平均", pool.average?.toFixed(1) ?? "—"]]
            : [[pool.topLabel, `${pool.topCount}`], ["限定", `${pool.limitedCount}`], ["平均", pool.average?.toFixed(1) ?? "—"]];
        const statsX = x + pad + inner * 0.44, cell = inner * 0.56 / 3;
        stats.forEach(([label, value], index) => {
            text(label, statsX + index * cell, y + 92, 21, theme.muted);
            text(value, statsX + index * cell, y + 130, 32, theme.text, { weight: 650 });
        });

        let cursor = y + 178;
        if (pool.pity) {
            pityMeter(pool.pity, `距上次 ${pool.topLabel}`, x + pad, cursor, inner);
            cursor += 56;
        }
        if (pool.limitedPity) {
            pityMeter(pool.limitedPity, "距上次限定", x + pad, cursor, inner);
            cursor += 56;
        }

        const exact = pool.rewards.filter(reward => !reward.atLeast);
        const stripH = 84;
        const chipsBottom = y + h - pad + 8 - (exact.length > 0 ? stripH + 12 : 0);
        text(`${pool.topLabel} 歷程（新到舊，數字為抽數）`, x + pad, cursor + 4, 21, theme.muted, { max: inner });
        chips(pool.rewards, x + pad, cursor + 18, inner, chipsBottom);
        if (exact.length > 0) {
            const fastest = exact.reduce((best, reward) => reward.pulls < best.pulls ? reward : best);
            const slowest = exact.reduce((worst, reward) => reward.pulls >= worst.pulls ? reward : worst);
            const half = (inner - 10) / 2, stripY = y + h - pad + 8 - stripH;
            extreme("最快", fastest, x + pad, stripY, half, stripH);
            extreme("最慢", slowest, x + pad + half + 10, stripY, half, stripH);
        }
    }

    function extreme(label, reward, x, y, w, h) {
        box(x, y, w, h, 12, theme.surface2);
        text(label, x + 16, y + 34, 22, theme.muted);
        const value = `${reward.pulls} 抽`;
        text(value, x + w - 16, y + 34, 26, theme.text, { weight: 700, align: "right" });
        text(reward.name, x + 16, y + 68, 23, theme.text, { max: w - 32 });
    }

    /** Picks the largest size and most columns where every reward fits whole. Failing that, names shorten before rewards go missing. */
    function chipGrid(rewards, w, room) {
        const gap = 6;
        const rowsFit = (rows, height) => rows * height + (rows - 1) * gap <= room;
        for (const size of [32, 30, 28, 26, 24, 23, 22]) {
            const height = Math.round(size * 1.74);
            const widest = Math.max(...rewards.map(reward =>
                width(reward.name, size) + width(`${reward.atLeast ? "≥" : ""}${reward.pulls}`, size, 700) + size * 1.5));
            for (const columns of [3, 2]) {
                const fit = Math.min(columns, rewards.length);
                if (widest <= (w - gap * (fit - 1)) / fit && rowsFit(Math.ceil(rewards.length / fit), height)) {
                    return { size, height, gap, columns: fit };
                }
            }
        }
        return { size: 22, height: 38, gap, columns: Math.min(3, rewards.length) };
    }

    /** Rewards as name and pulls in even columns, gold when limited. A short last row stays on the columns above it. */
    function chips(rewards, x, y, w, bottom) {
        if (rewards.length === 0) return;
        const { size, height, gap, columns } = chipGrid(rewards, w, bottom - y);
        const capacity = Math.max(1, Math.floor((bottom - y + gap) / (height + gap))) * columns;
        const clipped = rewards.length > capacity;
        const shown = clipped ? rewards.slice(0, capacity - 1) : rewards;
        const cell = (w - gap * (columns - 1)) / columns;
        const place = index => ({
            cx: x + (index % columns) * (cell + gap),
            cy: y + Math.floor(index / columns) * (height + gap),
        });
        shown.forEach((reward, index) => {
            const { cx, cy } = place(index);
            box(cx, cy, cell, height, 10, reward.limited ? LIMIT_TINT : theme.surface2, reward.limited ? GOLD : null);
            const ink = reward.limited ? LIMIT_INK : theme.text;
            const baseline = cy + height / 2 + size * 0.36;
            const value = `${reward.atLeast ? "≥" : ""}${reward.pulls}`;
            text(reward.name, cx + size * 0.5, baseline, size, ink, { max: cell - size * 1.5 - width(value, size, 700) });
            text(value, cx + cell - size * 0.5, baseline, size, ink, { weight: 700, align: "right" });
        });
        if (clipped) {
            const { cx, cy } = place(shown.length);
            text(`還有 ${rewards.length - shown.length} 個`, cx + cell / 2, cy + height / 2 + 8, 22, theme.muted, { align: "center" });
        }
    }

    function drawLuck(card, w, h) {
        const margin = 72;
        background(w, h);
        text("NTE 抽卡運氣", margin, 104, 44, theme.text, { weight: 700 });
        const facts = [period(card.firstDate, card.lastDate), `${number(card.totalPulls)} 抽`];
        if (card.maskedUid) facts.push(`UID ${card.maskedUid}`);
        text(facts.filter(Boolean).join("・"), margin, 146, 26, theme.muted);

        const overall = card.overall;
        if (!overall) {
            text("抽數還不足以判定運氣", w / 2, h / 2, 44, theme.muted, { align: "center" });
            footer(card, w, h, margin, "");
            return;
        }
        const size = 118;
        tag(overall.label, overall.tier, margin, 182, size);
        const tagWidth = width(overall.label, size, 700) + size * 1.44;
        text(signed(overall.sigma), margin + tagWidth + 36, 268, 72, theme.text, { weight: 700 });
        text(`理論上約勝過 ${overall.beatsPercent}% 的結果`, margin + tagWidth + 38, 318, 30, theme.muted);
        text(`實際 ${overall.hits} 個，期望 ${overall.expected.toFixed(1)} ± ${overall.standardDeviation.toFixed(1)} 個${overall.smallSample ? "・樣本少，僅供參考" : ""}`, margin, 412, 32, theme.text);

        bell(overall.sigma, overall.tier, margin, 440, w - margin * 2, 300, card.tierLabels);

        const rowsTop = 800, rowH = 72;
        card.pools.forEach((pool, index) => {
            const y = rowsTop + index * rowH;
            box(margin, y, w - margin * 2, rowH - 10, 12, theme.surface, theme.border);
            text(pool.label, margin + 24, y + 42, 30, theme.text, { weight: 650 });
            const luck = pool.luck;
            const detail = luck
                ? `${luck.smallSample ? "樣本少・" : ""}${luck.hits} 個／期望 ${luck.expected.toFixed(1)}・${signed(luck.sigma)}`
                : pool.totalPulls > 0 ? `${number(pool.totalPulls)} 抽` : "";
            const tagLabel = luck?.label ?? pool.luckNote ?? "—";
            const tagWidth = width(tagLabel, 24, 700) + 24 * 1.44;
            text(detail, w - margin - 20 - tagWidth - 28, y + 41, 26, theme.muted, { align: "right" });
            poolTag(pool, w - margin - 20, y + 12, 24);
        });
        footer(card, w, h, margin, "機率取自公開資料，百分比為常態近似。");
    }

    /** The bell of outcomes for this many pulls, shaded by tier, with the player marked. */
    function bell(sigma, tier, x, y, w, h, labels) {
        const range = 3.2, baseline = y + h - 46, labelY = y + 24;
        const toX = z => x + (z + range) / (range * 2) * w;
        const curve = z => Math.exp(-z * z / 2);
        const top = y + 58, height = baseline - top;
        const bands = [-range, ...STEP_AT, range];
        for (let band = 0; band < 7; band++) {
            const style = tierStyle(band);
            const from = bands[band], to = bands[band + 1];
            ctx.beginPath();
            ctx.moveTo(toX(from), baseline);
            for (let z = from; z <= to; z += 0.02) ctx.lineTo(toX(z), baseline - curve(z) * height);
            ctx.lineTo(toX(to), baseline - curve(to) * height);
            ctx.lineTo(toX(to), baseline);
            ctx.closePath();
            ctx.fillStyle = band === 3 ? theme.track : style.fill;
            ctx.fill();
            ctx.strokeStyle = theme.bg;
            ctx.lineWidth = 2;
            ctx.stroke();
        }
        ctx.fillStyle = theme.border;
        ctx.fillRect(x, baseline, w, 2);
        STEPS.forEach((step, index) => {
            const at = toX(STEP_AT[index]);
            ctx.fillStyle = theme.muted;
            ctx.fillRect(at - 1, baseline, 2, 10);
            // The two innermost marks sit too close for full labels at this size.
            if (Math.abs(STEP_AT[index]) !== 0.5) text(step, at, baseline + 34, 22, theme.muted, { align: "center" });
        });
        text("±0.5σ", toX(0), baseline + 34, 22, theme.muted, { align: "center" });

        const clamped = Math.max(-range + 0.08, Math.min(range - 0.08, sigma));
        const markX = toX(clamped);
        const markTop = labelY - 8;
        ctx.fillStyle = theme.text;
        ctx.fillRect(markX - 2, markTop, 4, baseline - markTop);
        ctx.beginPath();
        ctx.arc(markX, markTop, 9, 0, Math.PI * 2);
        ctx.fill();
        const onLeft = markX > x + w * 0.72;
        text(`你在這裡・${labels[tier]}`, markX + (onLeft ? -18 : 18), labelY, 26, theme.text, { weight: 700, align: onLeft ? "right" : "left" });
    }

    function drawPool(card, pool, w, h) {
        const margin = 64;
        background(w, h);
        text(pool.label, margin, 104, 60, theme.text, { weight: 700, max: w - margin * 2 - (pool.context ? 0 : 200) });
        if (pool.luck) tag(pool.luck.label, pool.luck.tier, w - margin, 54, 40, { right: true });
        else if (pool.luckNote) note(pool.luckNote, w - margin, 62, 30, { right: true });
        const facts = [pool.context, period(pool.firstDate, pool.lastDate)];
        if (pool.luck) facts.push(`${pool.luck.hits} 個／期望 ${pool.luck.expected.toFixed(1)} ± ${pool.luck.standardDeviation.toFixed(1)}（${signed(pool.luck.sigma)}）`);
        if (pool.luck?.smallSample) facts.push("樣本少");
        if (card.maskedUid) facts.push(`UID ${card.maskedUid}`);
        text(facts.filter(Boolean).join("・"), margin, 150, 25, theme.muted, { max: w - margin * 2 });

        if (pool.totalPulls === 0) {
            text("尚無紀錄", w / 2, h / 2, 44, theme.muted, { align: "center" });
            footer(card, w, h, margin, "");
            return;
        }

        const tiles = [
            ["總抽數", number(pool.totalPulls)],
            [pool.topLabel, `${pool.topCount}`],
            pool.limitedCount === null ? ["最短／最長", `${pool.shortest ?? "—"}／${pool.longest ?? "—"}`] : ["限定", `${pool.limitedCount}`],
            ["平均間隔", pool.average?.toFixed(1) ?? "—"],
        ];
        const gap = 14, tileW = (w - margin * 2 - gap * 3) / 4, tileY = 178, tileH = 122;
        tiles.forEach(([label, value], index) => {
            const x = margin + index * (tileW + gap);
            box(x, tileY, tileW, tileH, 14, theme.surface, theme.border);
            text(label, x + 20, tileY + 40, 23, theme.muted);
            text(value, x + 20, tileY + 96, 44, theme.accent, { weight: 700, max: tileW - 34 });
        });

        let cursor = tileY + tileH + 44;
        if (pool.pity) {
            pityMeter(pool.pity, `距上次 ${pool.topLabel}`, margin, cursor, w - margin * 2);
            cursor += 58;
        }
        if (pool.limitedPity) {
            pityMeter(pool.limitedPity, "距上次限定", margin, cursor, w - margin * 2);
            cursor += 58;
        }

        text(`${pool.topLabel} 出貨歷程`, margin, cursor + 18, 32, theme.text, { weight: 700 });
        const hasLimited = pool.rewards.some(reward => reward.limited);
        if (hasLimited) {
            box(w - margin - 78, cursor - 2, 22, 22, 5, GOLD);
            text("限定", w - margin, cursor + 16, 22, theme.muted, { align: "right" });
        }
        bars(pool, margin, cursor + 40, w - margin * 2, h - 70 - (cursor + 40));
        footer(card, w, h, margin, pool.context ? "新到舊；抽數與期望都含前期帶入的保底。" : "新到舊；≥ 表示更早的抽數可能不在紀錄內。");
    }

    function bars(pool, x, y, w, h) {
        const scale = pool.scale;
        const rise = pool.risesFrom;
        const minRow = 34, maxRow = BAR_ROW;
        const capacity = Math.floor((h - 8) / minRow);
        const clipped = pool.rewards.length > capacity;
        const count = clipped ? capacity - 1 : pool.rewards.length;
        const row = Math.min(maxRow, count > 0 ? (h - 8 - (clipped ? minRow : 0)) / count : maxRow);
        const nameW = 280, valueW = 74;
        const barX = x + nameW + 12, barW = w - nameW - valueW - 24;
        const thickness = Math.min(24, row - 10);
        pool.rewards.slice(0, count).forEach((reward, index) => {
            const cy = y + index * row;
            const mid = cy + row / 2;
            text(reward.name, x, mid + 9, 25, theme.text, { max: nameW });
            box(barX, mid - thickness / 2, barW, thickness, 6, theme.track);
            const length = Math.max(thickness, barW * Math.min(1, reward.pulls / scale));
            box(barX, mid - thickness / 2, length, thickness, 6, reward.limited ? GOLD : theme.bar);
            text(`${reward.atLeast ? "≥" : ""}${reward.pulls}`, x + w, mid + 9, 26, theme.text, { weight: 700, align: "right" });
        });
        const chartBottom = y + count * row;
        if (rise) {
            const at = barX + barW * rise / scale;
            ctx.fillStyle = theme.text;
            ctx.globalAlpha = 0.55;
            ctx.fillRect(at - 1, y - 4, 2, chartBottom - y + 4);
            ctx.globalAlpha = 1;
            text(`第 ${rise} 抽起機率上升`, at, y - 10, 19, theme.muted, { align: "center" });
        }
        if (clipped) {
            text(`還有 ${pool.rewards.length - count} 個較舊的 ${pool.topLabel}`, x, chartBottom + 26, 23, theme.muted);
        }
    }

    /** A pool with few rewards gets a shorter image rather than a page of empty space. */
    function poolHeight(pool) {
        const meters = (pool.pity ? 58 : 0) + (pool.limitedPity ? 58 : 0);
        const needed = 344 + meters + 40 + pool.rewards.length * BAR_ROW + 78;
        return Math.max(POOL_MIN_HEIGHT, Math.min(POOL_MAX_HEIGHT, needed));
    }

    function draw(canvas, card, template, themeName) {
        const [w, h] = template === "pool" ? [POOL_WIDTH, poolHeight(card.selected)] : SIZES[template];
        canvas.width = w;
        canvas.height = h;
        ctx = canvas.getContext("2d");
        theme = THEMES[themeName] ?? THEMES.sakura;
        if (template === "luck") drawLuck(card, w, h);
        else if (template === "pool") drawPool(card, card.selected, w, h);
        else drawOverview(card, w, h);
    }

    /** The canvas as PNG bytes, as base64. */
    const png = canvas => canvas.toDataURL("image/png").split(",")[1];

    return { draw, png };
})();
