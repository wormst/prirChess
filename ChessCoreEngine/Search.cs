using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using MPI;
using System.Linq;

namespace ChessEngine.Engine
{
    public static class Search
    {
        internal static int progress;
		
		private static int piecesRemaining;
		
        [Serializable]
        public struct Position
        {
            internal byte SrcPosition;
            internal byte DstPosition;
            internal int Score;
            //internal bool TopSort;
            internal string Move;

            public new string ToString()
            {
                return Move;
            }
        }

       

        private static readonly Position[,] KillerMove = new Position[3,20];
        private static int kIndex;

        private static int Sort(Position s2, Position s1)
        {
            return (s1.Score).CompareTo(s2.Score);
        }

        private static int Sort(Board s2, Board s1)
        {
            return (s1.Score).CompareTo(s2.Score);
        }

        private static int SideToMoveScore(int score, ChessPieceColor color)
        {
            if (color == ChessPieceColor.Black)
                return -score;

            return score;
        }

        internal static MoveContent IterativeSearch(Board examineBoard, byte depth, List<OpeningMove> currentGameBook)
        {
            Intracommunicator comm = Communicator.world;
            int numOfTasks = comm.Size;
            
            MoveContent bestMove = new MoveContent();

            //We are going to store our result boards here           
            ResultBoards succ = GetSortValidMoves(examineBoard);

            if ((byte)succ.Positions.Count == 1)
            {
                //I only have one move
                return succ.Positions[0].LastMove;
            }
            
            int numOfPositions = succ.Positions.Count;
            int positionsChunkSizeWhole = numOfPositions / numOfTasks;
            int positionsChunkSizeRest = numOfPositions - numOfTasks * positionsChunkSizeWhole;

                                        Console.WriteLine("Total number of tasks = " + numOfTasks.ToString());
                                        Console.WriteLine("Total valid moves = " + numOfPositions.ToString());
                                        

            int[] tasksPosChunkSize = new int[numOfTasks];
            for (int i = 0; i < numOfTasks; i++)
            {
                tasksPosChunkSize[i] = positionsChunkSizeWhole;
                if(positionsChunkSizeRest > 0)
                {
                    tasksPosChunkSize[i] += 1;
                    positionsChunkSizeRest--;
                }

                                        Console.WriteLine("tasksPosChunkSize[" + i.ToString() + "] = " + tasksPosChunkSize[i].ToString());
            }

            List<List<Board>> tasksPos = new List<List<Board>>();

                                        Console.WriteLine("All availible moves:");
                                        foreach (Board pos in succ.Positions)
                                        {
                                            Console.WriteLine(pos.ToString());
                                        }
               
            int idx = 0;
            for (int i = 0; i < numOfTasks; i++)
            {
                tasksPos.Add(succ.Positions.GetRange(idx, tasksPosChunkSize[i]));
                idx += tasksPosChunkSize[i];

                                        Console.WriteLine("Moves availible for task " + i.ToString() + " :");
                                        foreach (Board pos in tasksPos[i])
                                        {
                                            Console.WriteLine(pos.ToString());
                                        }
            }

            List<InterProcessData> dataToSend = new List<InterProcessData>();

            for ( int i = 0; i < numOfTasks; i++ )
            {
                dataToSend.Add(new InterProcessData
                {
                    pos = tasksPos[i],
                    depth = depth,
                    GameBook = currentGameBook,
                    ExamineBoard = examineBoard
                });
            }

            //wyslij do innych
            for (int i = 1; i < numOfTasks; i++)
            {
                comm.Send<InterProcessData>(dataToSend[i], i, 0);
            }

            MoveContent bestMoveOfThisThreadProbably = SzukajSzukaj(examineBoard, dataToSend[0].pos, dataToSend[0].depth, currentGameBook);
            List<MoveContent> bestMovesFromOtherThreads = new List<MoveContent>();
            bestMovesFromOtherThreads.Add(bestMoveOfThisThreadProbably);
            for (int i = 1; i < numOfTasks; i++)
            {
                bestMovesFromOtherThreads.Add(comm.Receive<MoveContent>(i, 0));
            }

            return bestMovesFromOtherThreads.OrderByDescending(m => m.Score).First();
        }

        public static MoveContent SzukajSzukaj(Board examineBoard, List<Board> positions, int depth, List<OpeningMove> currentGameBook)
        {
            List<Position> pvChild = new List<Position>();
            int alpha = -400000000;
            const int beta = 400000000;

            MoveContent bestMove = new MoveContent();

            int nodesSearched = 0;
            int nodesQuiessence = 0;

            //Can I make an instant mate?
            foreach (Board pos in positions)
            {
                //TODO: Send each (or in packs of few i.e.) board to different process, and finish if any returned bigger value
                int value = -AlphaBeta(pos, 1, -beta, -alpha, ref nodesSearched, ref nodesQuiessence, ref pvChild, true);

                if (value >= 32767)
                {
                    pos.LastMove.Score = value;
                    return pos.LastMove;
                }
            }

            int currentBoard = 0;

            alpha = -400000000;

            positions.Sort(Sort);

            depth--;

            int plyDepthReached = ModifyDepth((byte)depth, positions.Count());

            foreach (Board pos in positions)
            {
                currentBoard++;

                progress = (int)((currentBoard / (decimal)positions.Count) * 100);

                pvChild = new List<Position>();

                int value = -AlphaBeta(pos, (byte)depth, -beta, -alpha, ref nodesSearched, ref nodesQuiessence, ref pvChild, false);

                if (value >= 32767)
                {
                    pos.LastMove.Score = value;
                    return pos.LastMove;
                }

                if (examineBoard.RepeatedMove == 2)
                {
                    string fen = Board.Fen(true, pos);

                    foreach (OpeningMove move in currentGameBook)
                    {
                        if (move.EndingFEN == fen)
                        {
                            value = 0;
                            break;
                        }
                    }
                }

                pos.Score = value;

                //If value is greater then alpha this is the best board
                if (value > alpha || alpha == -400000000)
                {
                    //pvLine = pos.LastMove.ToString();

                    //foreach (Position pvPos in pvChild)
                    //{
                    //    pvLine += " " + pvPos.ToString();
                    //}

                    alpha = value;
                    bestMove = pos.LastMove;
                    bestMove.Score = value;
                }
            }

            plyDepthReached++;
            progress = 100;

            return bestMove;
        }

        private static ResultBoards GetSortValidMoves(Board examineBoard)
        {
            ResultBoards succ = new ResultBoards
                                    {
                                        Positions = new List<Board>(30)
                                    };

            piecesRemaining = 0;

            for (byte x = 0; x < 64; x++)
            {
                Square sqr = examineBoard.Squares[x];

                //Make sure there is a piece on the square
                if (sqr.Piece == null)
                    continue;

                piecesRemaining++;

                //Make sure the color is the same color as the one we are moving.
                if (sqr.Piece.PieceColor != examineBoard.WhoseMove)
                    continue;

                //For each valid move for this piece
                foreach (byte dst in sqr.Piece.ValidMoves)
                {
                    //We make copies of the board and move so that we can move it without effecting the parent board
                    Board board = examineBoard.FastCopy();

                    //Make move so we can examine it
                    Board.MovePiece(board, x, dst, ChessPieceType.Queen);

                    //We Generate Valid Moves for Board
                    PieceValidMoves.GenerateValidMoves(board);

                    //Invalid Move
                    if (board.WhiteCheck && examineBoard.WhoseMove == ChessPieceColor.White)
                    {
                        continue;
                    }

                    //Invalid Move
                    if (board.BlackCheck && examineBoard.WhoseMove == ChessPieceColor.Black)
                    {
                        continue;
                    }

                    //We calculate the board score
                    Evaluation.EvaluateBoardScore(board);

                    //Invert Score to support Negamax
                    board.Score = SideToMoveScore(board.Score, board.WhoseMove);

                    succ.Positions.Add(board);
                }
            }

            succ.Positions.Sort(Sort);
            return succ;
        }

        private static int AlphaBeta(Board examineBoard, byte depth, int alpha, int beta, ref int nodesSearched, ref int nodesQuiessence, ref List<Position> pvLine, bool extended)
        {
            nodesSearched++;

            if (examineBoard.FiftyMove >= 50 || examineBoard.RepeatedMove >= 3)
                return 0;

            //End Main Search with Quiescence
            if (depth == 0)
            {
                if (!extended && examineBoard.BlackCheck || examineBoard.WhiteCheck)
                {
                    depth++;
                    extended = true;
                }
                else
                {
                    //Perform a Quiessence Search
                    return Quiescence(examineBoard, alpha, beta, ref nodesQuiessence);
                }
            }

            List<Position> positions = EvaluateMoves(examineBoard, depth);

            if (examineBoard.WhiteCheck || examineBoard.BlackCheck || positions.Count == 0)
            {
                if (SearchForMate(examineBoard.WhoseMove, examineBoard, ref examineBoard.BlackMate, ref examineBoard.WhiteMate, ref examineBoard.StaleMate))
                {
                    if (examineBoard.BlackMate)
                    {
                        if (examineBoard.WhoseMove == ChessPieceColor.Black)
                            return -32767-depth;

                        return 32767 + depth;
                    }
                    if (examineBoard.WhiteMate)
                    {
                        if (examineBoard.WhoseMove == ChessPieceColor.Black)
                            return 32767 + depth;

                        return -32767 - depth;
                    }

                    //If Not Mate then StaleMate
                    return 0;
                }
            }

            positions.Sort(Sort);

            foreach (Position move in positions)
            {
                List<Position> pvChild = new List<Position>();

                //Make a copy
                Board board = examineBoard.FastCopy();

                //Move Piece
                Board.MovePiece(board, move.SrcPosition, move.DstPosition, ChessPieceType.Queen);

                //We Generate Valid Moves for Board
                PieceValidMoves.GenerateValidMoves(board);

                if (board.BlackCheck)
                {
                    if (examineBoard.WhoseMove == ChessPieceColor.Black)
                    {
                        //Invalid Move
                        continue;
                    }
                }

                if (board.WhiteCheck)
                {
                    if (examineBoard.WhoseMove == ChessPieceColor.White)
                    {
                        //Invalid Move
                        continue;
                    }
                }

                int value = -AlphaBeta(board, (byte)(depth - 1), -beta, -alpha, ref nodesSearched, ref nodesQuiessence, ref pvChild, extended);

                if (value >= beta)
                {
                    KillerMove[kIndex, depth].SrcPosition = move.SrcPosition;
                    KillerMove[kIndex, depth].DstPosition = move.DstPosition;

                    kIndex = ((kIndex + 1) % 2);

                    
                    return beta;
                }
                if (value > alpha)
                {
                    Position pvPos = new Position();

                    pvPos.SrcPosition = board.LastMove.MovingPiecePrimary.SrcPosition;
                    pvPos.DstPosition = board.LastMove.MovingPiecePrimary.DstPosition;
                    pvPos.Move = board.LastMove.ToString();

                    pvChild.Insert(0, pvPos);

                    pvLine = pvChild;

                    alpha = (int)value;
                }
            }

            return alpha;
        }

        private static int Quiescence(Board examineBoard, int alpha, int beta, ref int nodesSearched)
        {
            nodesSearched++;

            //Evaluate Score
            Evaluation.EvaluateBoardScore(examineBoard);

            //Invert Score to support Negamax
            examineBoard.Score = SideToMoveScore(examineBoard.Score, examineBoard.WhoseMove);

            if (examineBoard.Score >= beta)
                return beta;

            if (examineBoard.Score > alpha)
                alpha = examineBoard.Score;

            
            List<Position> positions;
          

            if (examineBoard.WhiteCheck || examineBoard.BlackCheck)
            {
                positions = EvaluateMoves(examineBoard, 0);
            }
            else
            {
                positions = EvaluateMovesQ(examineBoard);    
            }

            if (positions.Count == 0)
            {
                return examineBoard.Score;
            }
            
            positions.Sort(Sort);

            foreach (Position move in positions)
            {
                if (StaticExchangeEvaluation(examineBoard.Squares[move.DstPosition]) >= 0)
                {
                    continue;
                }

                //Make a copy
                Board board = examineBoard.FastCopy();

                //Move Piece
                Board.MovePiece(board, move.SrcPosition, move.DstPosition, ChessPieceType.Queen);

                //We Generate Valid Moves for Board
                PieceValidMoves.GenerateValidMoves(board);

                if (board.BlackCheck)
                {
                    if (examineBoard.WhoseMove == ChessPieceColor.Black)
                    {
                        //Invalid Move
                        continue;
                    }
                }

                if (board.WhiteCheck)
                {
                    if (examineBoard.WhoseMove == ChessPieceColor.White)
                    {
                        //Invalid Move
                        continue;
                    }
                }

                int value = -Quiescence(board, - beta, -alpha, ref nodesSearched);

                if (value >= beta)
                {
                    KillerMove[2, 0].SrcPosition = move.SrcPosition;
                    KillerMove[2, 0].DstPosition = move.DstPosition;

                    return beta;
                }
                if (value > alpha)
                {
                    alpha = value;
                }
            }

            return alpha;
        }

        private static List<Position> EvaluateMoves(Board examineBoard, byte depth)
        {

            //We are going to store our result boards here           
            List<Position> positions = new List<Position>();

            //bool foundPV = false;


            for (byte x = 0; x < 64; x++)
            {
                Piece piece = examineBoard.Squares[x].Piece;

                //Make sure there is a piece on the square
                if (piece == null)
                    continue;

                //Make sure the color is the same color as the one we are moving.
                if (piece.PieceColor != examineBoard.WhoseMove)
                    continue;

                //For each valid move for this piece
                foreach (byte dst in piece.ValidMoves)
                {
                    Position move = new Position();

                    move.SrcPosition = x;
                    move.DstPosition = dst;
				
                    if (move.SrcPosition == KillerMove[0, depth].SrcPosition && move.DstPosition == KillerMove[0, depth].DstPosition)
                    {
                        //move.TopSort = true;
                        move.Score += 5000;
                        positions.Add(move);
                        continue;
                    }
                    if (move.SrcPosition == KillerMove[1, depth].SrcPosition && move.DstPosition == KillerMove[1, depth].DstPosition)
                    {
                        //move.TopSort = true;
                        move.Score += 5000;
                        positions.Add(move);
                        continue;
                    }

                    Piece pieceAttacked = examineBoard.Squares[move.DstPosition].Piece;

                    //If the move is a capture add it's value to the score
                    if (pieceAttacked != null)
                    {
                        move.Score += pieceAttacked.PieceValue;

                        if (piece.PieceValue < pieceAttacked.PieceValue)
                        {
                            move.Score += pieceAttacked.PieceValue - piece.PieceValue;
                        }
                    }

                    if (!piece.Moved)
                    {
                        move.Score += 10;
                    }

                    move.Score += piece.PieceActionValue;

                    //Add Score for Castling
                    if (!examineBoard.WhiteCastled && examineBoard.WhoseMove == ChessPieceColor.White)
                    {

                        if (piece.PieceType == ChessPieceType.King)
                        {
                            if (move.DstPosition != 62 && move.DstPosition != 58)
                            {
                                move.Score -= 40;
                            }
                            else
                            {
                                move.Score += 40;
                            }
                        }
                        if (piece.PieceType == ChessPieceType.Rook)
                        {
                            move.Score -= 40;
                        }
                    }

                    if (!examineBoard.BlackCastled && examineBoard.WhoseMove == ChessPieceColor.Black)
                    {
                        if (piece.PieceType == ChessPieceType.King)
                        {
                            if (move.DstPosition != 6 && move.DstPosition != 2)
                            {
                                move.Score -= 40;
                            }
                            else
                            {
                                move.Score += 40;
                            }
                        }
                        if (piece.PieceType == ChessPieceType.Rook)
                        {
                            move.Score -= 40;
                        }
                    }

                    positions.Add(move);
                }
            }

            return positions;
        }

        private static List<Position> EvaluateMovesQ(Board examineBoard)
        {
            //We are going to store our result boards here           
            List<Position> positions = new List<Position>();

            for (byte x = 0; x < 64; x++)
            {
                Piece piece = examineBoard.Squares[x].Piece;

                //Make sure there is a piece on the square
                if (piece == null)
                    continue;

                //Make sure the color is the same color as the one we are moving.
                if (piece.PieceColor != examineBoard.WhoseMove)
                    continue;

                //For each valid move for this piece
                foreach (byte dst in piece.ValidMoves)
                {
                    if (examineBoard.Squares[dst].Piece == null)
                    {
                        continue;
                    }

                    Position move = new Position();

                    move.SrcPosition = x;
                    move.DstPosition = dst;

                    if (move.SrcPosition == KillerMove[2, 0].SrcPosition && move.DstPosition == KillerMove[2, 0].DstPosition)
                    {
                        //move.TopSort = true;
                        move.Score += 5000;
                        positions.Add(move);
                        continue;
                    }

                    Piece pieceAttacked = examineBoard.Squares[move.DstPosition].Piece;

                    move.Score += pieceAttacked.PieceValue;

                    if (piece.PieceValue < pieceAttacked.PieceValue)
                    {
                        move.Score += pieceAttacked.PieceValue - piece.PieceValue;
                    }

                    move.Score += piece.PieceActionValue;


                    positions.Add(move);
                }
            }

            return positions;
        }

        internal static bool SearchForMate(ChessPieceColor movingSide, Board examineBoard, ref bool blackMate, ref bool whiteMate, ref bool staleMate)
        {
            bool foundNonCheckBlack = false;
            bool foundNonCheckWhite = false;

            for (byte x = 0; x < 64; x++)
            {
                Square sqr = examineBoard.Squares[x];

                //Make sure there is a piece on the square
                if (sqr.Piece == null)
                    continue;

                //Make sure the color is the same color as the one we are moving.
                if (sqr.Piece.PieceColor != movingSide)
                    continue;

                //For each valid move for this piece
                foreach (byte dst in sqr.Piece.ValidMoves)
                {

                    //We make copies of the board and move so that we can move it without effecting the parent board
                    Board board = examineBoard.FastCopy();

                    //Make move so we can examine it
                    Board.MovePiece(board, x, dst, ChessPieceType.Queen);

                    //We Generate Valid Moves for Board
                    PieceValidMoves.GenerateValidMoves(board);

                    if (board.BlackCheck == false)
                    {
                        foundNonCheckBlack = true;
                    }
                    else if (movingSide == ChessPieceColor.Black)
                    {
                        continue;
                    }

                    if (board.WhiteCheck == false )
                    {
                        foundNonCheckWhite = true;
                    }
                    else if (movingSide == ChessPieceColor.White)
                    {
                        continue;
                    }
                }
            }

            if (foundNonCheckBlack == false)
            {
                if (examineBoard.BlackCheck)
                {
                    blackMate = true;
                    return true;
                }
                if (!examineBoard.WhiteMate && movingSide != ChessPieceColor.White)
                {
                    staleMate = true;
                    return true;
                }
            }

            if (foundNonCheckWhite == false)
            {
                if (examineBoard.WhiteCheck)
                {
                    whiteMate = true;
                    return true;
                }
                if (!examineBoard.BlackMate && movingSide != ChessPieceColor.Black)
                {
                    staleMate = true;
                    return true;
                }
            }
            
            //Trace.WriteLine($"Method {nameof(SearchForMate)} took  ms.")
            return false;
        }

        private static byte ModifyDepth(byte depth, int possibleMoves)
        {
            if (possibleMoves <= 20 || piecesRemaining < 14)
            {
                if (possibleMoves <= 10 || piecesRemaining < 6)
                {
                    depth += 1;
                }

                depth += 1;
            }

            return depth;
        }

        private static int StaticExchangeEvaluation(Square examineSquare)
        {
            if (examineSquare.Piece == null)
            {
                return 0;
            }
            if (examineSquare.Piece.AttackedValue == 0)
            {
                return 0;
            }

            return examineSquare.Piece.PieceActionValue - examineSquare.Piece.AttackedValue + examineSquare.Piece.DefendedValue;
        }

    }
}
