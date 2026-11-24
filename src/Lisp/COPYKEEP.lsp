;; Копирует выбранные объекты на слой 0 с обычным предпросмотром команды COPY.
;; Копирование выполняет штатная команда COPY (объекты "едут" за курсором),
;; затем у созданных копий внешний вид фиксируется по исходному слою
;; (цвет, тип и толщина линии "по слою"), и они переносятся на слой 0.
;; Свойства меняются через ActiveX (без entmod).

(defun ck-try (func args / r)
  (setq r (vl-catch-all-apply func args))
  (not (vl-catch-all-error-p r))
)

;; Фиксирует свойства "по слою" и переносит объект на слой 0
(defun ck-fix (obj lyrs / lname srcL)
  (setq lname (vl-catch-all-apply 'vla-get-layer (list obj)))
  (setq srcL (if (vl-catch-all-error-p lname)
               nil
               (vl-catch-all-apply 'vla-item (list lyrs lname))
             )
  )
  (if (and srcL (not (vl-catch-all-error-p srcL)))
    (progn
      ;; Цвет
      (if (ck-try 'vla-get-color (list obj))
        (if (= (vla-get-color obj) 256)
          (ck-try 'vla-put-truecolor (list obj (vla-get-truecolor srcL)))
        )
      )
      ;; Тип линии
      (if (ck-try 'vla-get-linetype (list obj))
        (if (= (strcase (vla-get-linetype obj)) "BYLAYER")
          (ck-try 'vla-put-linetype (list obj (vla-get-linetype srcL)))
        )
      )
      ;; Толщина линии
      (if (ck-try 'vla-get-lineweight (list obj))
        (if (= (vla-get-lineweight obj) -1)
          (ck-try 'vla-put-lineweight (list obj (vla-get-lineweight srcL)))
        )
      )
    )
  )
  ;; Перенос на слой 0
  (ck-try 'vla-put-layer (list obj "0"))
)

(defun shiza:run-copykeep (/ ss p1 last e typ n lyrs doc oldmode *error*)
  (vl-load-com)
  (setq n 0)
  ;; Обработчик Esc/ошибок: восстанавливаем COPYMODE и выходим тихо
  (defun *error* (msg)
    (if (and oldmode (not (vl-catch-all-error-p oldmode)))
      (setvar "COPYMODE" oldmode)
    )
    (if (and msg
             (not (wcmatch (strcase msg) "*CANCEL*,*ОТМЕН*,*EXIT*,*QUIT*")))
      (princ (strcat "\nОшибка: " msg))
    )
    (princ)
  )
  (setq ss (ssget))
  (if ss
    (progn
      (setq p1 (getpoint "\nБазовая точка: "))
      (if p1
        (progn
          (setq doc  (vla-get-ActiveDocument (vlax-get-acad-object))
                lyrs (vla-get-Layers doc)
                last (entlast)
          )
          ;; Одиночное копирование (чтобы команда завершилась после одной точки)
          (setq oldmode (vl-catch-all-apply 'getvar '("COPYMODE")))
          (if (not (vl-catch-all-error-p oldmode))
            (setvar "COPYMODE" 1)
          )
          ;; Штатная команда COPY: предпросмотр при выборе точки вставки
          (command "_.COPY" ss "" p1 pause)
          (if (not (vl-catch-all-error-p oldmode))
            (setvar "COPYMODE" oldmode)
          )
          ;; Обработка созданных копий
          (setq e (if last (entnext last) (entnext)))
          (while e
            (setq typ (cdr (assoc 0 (entget e))))
            (if (not (member typ '("ATTRIB" "VERTEX" "SEQEND")))
              (progn
                (ck-fix (vlax-ename->vla-object e) lyrs)
                (setq n (1+ n))
              )
            )
            (setq e (entnext e))
          )
          (princ (strcat "\nСкопировано на слой 0: " (itoa n)))
        )
      )
    )
  )
  (princ)
)
