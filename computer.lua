-- Initial template for unprogrammed AU-08 computers. Existing installed copies are preserved.
-- E edits the targeted computer. Save in build mode; programs start when entering game mode.
-- Input/output indices start at 1. F8 runs/stops the targeted computer; F9 stops all.
function tick()
    output(1, input(1) * 2)
end
